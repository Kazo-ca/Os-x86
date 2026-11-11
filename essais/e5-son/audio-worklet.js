// Essai E5 — processeur AudioWorklet à deux usages (code jetable de mesure) :
//  - mode "meter" : 1 entrée stéréo (sortie du DAC v86 natif) -> mesure et passe-plat ;
//  - mode "dac"   : 0 entrée ; reçoit les blocs SB16 ([Float32Array L, Float32Array R] au taux source)
//                   directement du Web Worker par un MessagePort, les rééchantillonne (interpolation
//                   linéaire, pas de sinc comme v86), et demande la suite au worker ("pump") avec un
//                   compteur de blocs reçus (garde anti-requêtes périmées, PR v86 #1661).
// Mesure dans les deux modes : RMS par bloc de 256 trames (L et R), premier bloc non silencieux,
// trames vides rendues alors que le DAC est actif (sous-alimentation), trous.
class E5Processor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const o = options.processorOptions || {};
    this.mode = o.mode || "meter";
    this.blockFrames = 256;
    this.maxBlocks = o.maxBlocks || 40000; // 40000 * 256 / 48000 = 213 s
    this.histL = new Float32Array(this.maxBlocks);
    this.histR = new Float32Array(this.maxBlocks);
    this.histT = new Float64Array(this.maxBlocks); // currentTime au début du bloc
    this.reset();
    // DAC
    this.queue = [];
    this.queuedFrames = 0;
    this.srcRate = 22050;
    this.pos = 0; // position fractionnaire dans queue[0]
    this.enabled = false;
    this.pumpSeq = 0;
    this.watermark = 1024 + Math.round(((o.leadMs || 0) / 1000) * sampleRate); // trames de sortie ; 1024 comme le DACProcessor de v86, + avance ("lead") optionnelle
    this.port.onmessage = (e) => this.onMessage(e.data, this.port);
  }
  reset() {
    this.nBlocks = 0;
    this.blockFill = 0;
    this.sumL = 0; this.sumR = 0;
    this.blockStart = currentTime;
    this.firstSoundTime = null;
    this.firstQueueTime = null;
    this.receivedCount = 0;
    this.receivedFrames = 0;
    this.underrunFrames = 0;
    this.underrunEvents = []; // {t, frames} fusionnés
    this.pumpCount = 0;
    this.renderedFrames = 0;
    this.lastUnderrunBlock = -1;
  }
  onMessage(m, port) {
    switch (m.type) {
      case "port": // mode dac : port direct vers le worker
        this.src = m.port;
        this.src.onmessage = (e) => this.onMessage(e.data, this.src);
        this.src.postMessage({ type: "hello" });
        break;
      case "queue":
        this.receivedCount++;
        this.receivedFrames += m.value[0].length;
        if (this.firstQueueTime === null) this.firstQueueTime = currentTime;
        this.queue.push(m.value);
        this.queuedFrames += m.value[0].length;
        break;
      case "rate": this.srcRate = m.value; break;
      case "enable": this.enabled = true; break;
      case "disable": this.enabled = false; break;
      case "reset": this.reset(); break;
      case "report": port.postMessage({ type: "report", id: m.id, report: this.report() }); break;
    }
  }
  report() {
    return {
      mode: this.mode, sampleRate, blockFrames: this.blockFrames, srcRate: this.srcRate,
      nBlocks: this.nBlocks, histL: this.histL.slice(0, this.nBlocks), histR: this.histR.slice(0, this.nBlocks), histT: this.histT.slice(0, this.nBlocks),
      firstSoundTime: this.firstSoundTime, firstQueueTime: this.firstQueueTime,
      receivedCount: this.receivedCount, receivedFrames: this.receivedFrames,
      underrunFrames: this.underrunFrames, underrunEvents: this.underrunEvents.slice(0, 500), underrunEventCount: this.underrunEvents.length,
      pumpCount: this.pumpCount, renderedFrames: this.renderedFrames, enabled: this.enabled, queuedFrames: this.queuedFrames, now: currentTime,
    };
  }
  meter(l, r, n) {
    for (let i = 0; i < n; i++) {
      const a = l[i], b = r ? r[i] : a;
      this.sumL += a * a; this.sumR += b * b;
      if (++this.blockFill === this.blockFrames) {
        if (this.nBlocks < this.maxBlocks) {
          const rl = Math.sqrt(this.sumL / this.blockFrames), rr = Math.sqrt(this.sumR / this.blockFrames);
          this.histL[this.nBlocks] = rl; this.histR[this.nBlocks] = rr; this.histT[this.nBlocks] = this.blockStart;
          if (this.firstSoundTime === null && (rl > 0.01 || rr > 0.01)) this.firstSoundTime = this.blockStart;
          this.nBlocks++;
        }
        this.blockFill = 0; this.sumL = 0; this.sumR = 0;
        this.blockStart = currentTime + (i + 1) / sampleRate;
      }
    }
  }
  fillFromQueue(outL, outR) {
    const n = outL.length;
    const ratio = this.srcRate / sampleRate;
    let empty = 0;
    for (let i = 0; i < n; i++) {
      if (!this.queue.length) { outL[i] = 0; outR[i] = 0; empty++; continue; }
      const cur = this.queue[0];
      const len = cur[0].length;
      const p = this.pos;
      const i0 = Math.floor(p);
      const frac = p - i0;
      const i1 = i0 + 1;
      let l1, r1;
      if (i1 < len) { l1 = cur[0][i1]; r1 = cur[1][i1]; }
      else if (this.queue.length > 1) { l1 = this.queue[1][0][0]; r1 = this.queue[1][1][0]; }
      else { l1 = cur[0][i0]; r1 = cur[1][i0]; }
      outL[i] = cur[0][i0] + (l1 - cur[0][i0]) * frac;
      outR[i] = cur[1][i0] + (r1 - cur[1][i0]) * frac;
      this.pos = p + ratio;
      while (this.queue.length && this.pos >= this.queue[0][0].length) {
        this.pos -= this.queue[0][0].length;
        this.queuedFrames -= this.queue[0][0].length;
        this.queue.shift();
      }
    }
    if (empty && this.enabled) {
      this.underrunFrames += empty;
      const t = currentTime;
      const last = this.underrunEvents[this.underrunEvents.length - 1];
      if (last && t - (last.t + last.frames / sampleRate) < 0.01) last.frames += empty;
      else this.underrunEvents.push({ t, frames: empty });
    }
    // Demande de données : au plus une par quantum, avec le nombre de blocs déjà reçus (garde #1661 côté worker).
    if (this.src && this.enabled && this.queuedFrames / ratio < this.watermark) {
      this.pumpCount++;
      this.src.postMessage({ type: "pump", seq: ++this.pumpSeq, received: this.receivedCount, t: currentTime });
    }
  }
  process(inputs, outputs) {
    const out = outputs[0];
    if (this.mode === "meter") {
      const inp = inputs[0];
      if (inp && inp.length) {
        const l = inp[0], r = inp[1] || inp[0];
        this.meter(l, r, l.length);
        this.renderedFrames += l.length;
        if (out && out.length) { out[0].set(l); if (out[1]) out[1].set(r); }
      } else if (out && out.length) {
        this.meter(out[0], out[1] || out[0], out[0].length); // silence
        this.renderedFrames += out[0].length;
      }
      return true;
    }
    const outL = out[0], outR = out[1] || out[0];
    this.fillFromQueue(outL, outR);
    this.meter(outL, outR, outL.length);
    this.renderedFrames += outL.length;
    return true;
  }
}
registerProcessor("e5-processor", E5Processor);
