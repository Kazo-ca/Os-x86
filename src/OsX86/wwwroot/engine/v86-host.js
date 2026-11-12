// Os-x86 engine host: the main-thread side of the v86 engine (milestone 0, work item 2).
//
// Imported by the C# side (JsEngineWorkerChannel) as ./_content/OsX86/engine/v86-host.js, the
// only JavaScript import Kazo OS allows an app (app-sdk.md: an app imports its own module and
// then calls it freely). createHost() starts the Web Worker that runs v86 (v86-worker.js) and
// relays messages both ways without looking at them: the C# side and the worker speak the JSON
// protocol described at the top of v86-worker.js. Nothing here touches the DOM except
// attachCanvas(), which hands an OffscreenCanvas to the worker.

/**
 * @typedef {Object} DotNetCallback
 * @property {(method: string, ...args: any[]) => Promise<any>} invokeMethodAsync
 */

/**
 * Starts the engine worker.
 * @param {DotNetCallback} callback  Receives every worker message as `OnWorkerMessage(json)`.
 * @param {{ workerUrl?: string, v86Url?: string }} [options]  Overrides for tests; by default
 *   the worker is ./v86-worker.js and v86 is ../v86/ next to this module.
 */
export function createHost(callback, options) {
  return new EngineHost(callback, options || {});
}

function sameOrigin(url, name) {
  if (url.origin !== new URL(import.meta.url).origin) {
    throw new Error(`${name} must be served from the origin of the engine module`);
  }
  return url;
}

class EngineHost {
  constructor(callback, options) {
    const base = import.meta.url;
    const v86 = sameOrigin(new URL(options.v86Url || "../v86/", base), "v86Url").href;
    const workerUrl = sameOrigin(new URL(options.workerUrl || "./v86-worker.js", base), "workerUrl");
    this.callback = callback;
    this.worker = new Worker(workerUrl, { name: "os-x86-engine" });
    this.worker.onmessage = (event) => this.deliver(event.data);
    this.worker.onerror = (event) => {
      const message = event && event.message ? event.message : "worker error";
      this.deliver(JSON.stringify({ type: "error", code: "worker-error", message, fatal: true }));
    };
    this.worker.postMessage({
      type: "init",
      v86: {
        libUrl: v86 + "libv86.js",
        wasmUrl: v86 + "v86.wasm",
        biosUrl: v86 + "seabios.bin",
        vgaBiosUrl: v86 + "vgabios.bin",
      },
    });
  }

  /** Forwards a protocol message (a JSON string built by the C# side) to the worker. */
  post(json) {
    this.worker.postMessage(json);
  }

  /**
   * Gives the worker a canvas to draw the guest screen on. The canvas element is transferred as
   * an OffscreenCanvas: after this call only the worker can draw on it (the display bridge of the
   * plan; work item 2 only resizes and clears it, the rendering comes with the graphical guests).
   */
  attachCanvas(canvas) {
    if (!canvas || typeof canvas.transferControlToOffscreen !== "function") {
      throw new Error("attachCanvas expects a canvas element");
    }
    const offscreen = canvas.transferControlToOffscreen();
    this.worker.postMessage({ type: "attach-canvas", canvas: offscreen }, [offscreen]);
  }

  detachCanvas() {
    this.worker.postMessage({ type: "detach-canvas" });
  }

  /** Kills the worker and its emulator at once (no clean stop; send the stop message first). */
  dispose() {
    this.worker.terminate();
  }

  deliver(json) {
    try {
      const result = this.callback.invokeMethodAsync("OnWorkerMessage", json);
      if (result && typeof result.catch === "function") {
        result.catch((error) => console.error("Os-x86 engine: the host rejected a worker message", error));
      }
    } catch (error) {
      console.error("Os-x86 engine: the host callback threw", error);
    }
  }
}
