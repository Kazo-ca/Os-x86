// Os-x86 storage host: the main-thread side of the storage worker (milestone 0, work item 3).
//
// Imported by the C# side (JsStorageWorkerChannel) as ./_content/OsX86/storage/storage-host.js,
// on the model of the engine host (engine/v86-host.js). createHost() starts the dedicated Web
// Worker that downloads, verifies and stores images in the OPFS (storage-worker.js) and relays
// messages both ways without looking at them: the C# side and the worker speak the JSON protocol
// described at the top of storage-worker.js. Nothing here touches the DOM.

/**
 * @typedef {Object} DotNetCallback
 * @property {(method: string, ...args: any[]) => Promise<any>} invokeMethodAsync
 */

/**
 * Starts the storage worker.
 * @param {DotNetCallback} callback  Receives every worker message as `OnWorkerMessage(json)`.
 * @param {{ workerUrl?: string }} [options]  Override for tests; by default the worker is
 *   ./storage-worker.js next to this module (same origin required).
 */
export function createHost(callback, options) {
  return new StorageHost(callback, options || {});
}

function sameOrigin(url, name) {
  if (url.origin !== new URL(import.meta.url).origin) {
    throw new Error(`${name} must be served from the origin of the storage module`);
  }
  return url;
}

class StorageHost {
  constructor(callback, options) {
    const workerUrl = sameOrigin(new URL(options.workerUrl || "./storage-worker.js", import.meta.url), "workerUrl");
    this.callback = callback;
    this.worker = new Worker(workerUrl, { name: "os-x86-storage" });
    this.worker.onmessage = (event) => this.deliver(event.data);
    this.worker.onerror = (event) => {
      const message = event && event.message ? event.message : "worker error";
      this.deliver(JSON.stringify({ type: "error", code: "worker-error", message }));
    };
  }

  /** Forwards a protocol command (a JSON string built by the C# side) to the worker. */
  post(json) {
    this.worker.postMessage(json);
  }

  /** Kills the worker at once; a download in progress keeps its verified chunks (the state is in the OPFS). */
  dispose() {
    this.worker.terminate();
  }

  deliver(json) {
    try {
      const result = this.callback.invokeMethodAsync("OnWorkerMessage", json);
      if (result && typeof result.catch === "function") {
        result.catch((error) => console.error("Os-x86 storage: the host rejected a worker message", error));
      }
    } catch (error) {
      console.error("Os-x86 storage: the host callback threw", error);
    }
  }
}
