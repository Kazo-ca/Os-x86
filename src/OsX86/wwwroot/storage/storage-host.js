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

/** The worker must be an http(s) script of this module's own directory: no blob: or data: URL gets OPFS access as the storage worker. */
function checkWorkerUrl(url) {
  const module = new URL(import.meta.url);
  const directory = module.href.slice(0, module.href.lastIndexOf("/") + 1);
  if ((url.protocol !== "https:" && url.protocol !== "http:") || url.origin !== module.origin || !url.href.startsWith(directory)) {
    throw new Error("workerUrl must be a script next to the storage module, on its origin");
  }
  return url;
}

class StorageHost {
  constructor(callback, options) {
    const workerUrl = checkWorkerUrl(new URL(options.workerUrl || "./storage-worker.js", import.meta.url));
    this.callback = callback;
    this.disposed = false;
    this.worker = new Worker(workerUrl, { name: "os-x86-storage" });
    this.worker.onmessage = (event) => this.deliver(event.data);
    // Both are fatal for the C# side (an error without id fails every pending request): a script that
    // does not load, an uncaught exception, a message that cannot be cloned.
    this.worker.onerror = (event) => {
      const message = event && event.message ? event.message : "the storage worker failed to load or threw";
      this.deliver(JSON.stringify({ type: "error", code: "worker-error", message }));
    };
    this.worker.onmessageerror = () => {
      this.deliver(JSON.stringify({ type: "error", code: "protocol", message: "a worker message could not be deserialized" }));
    };
  }

  /** Forwards a protocol command (a JSON string built by the C# side) to the worker. */
  post(json) {
    this.worker.postMessage(json);
  }

  /** Kills the worker at once; a download in progress keeps its verified chunks (the state is in the OPFS). */
  dispose() {
    this.disposed = true;
    this.worker.onmessage = null;
    this.worker.onerror = null;
    this.worker.onmessageerror = null;
    this.worker.terminate();
  }

  deliver(json) {
    if (this.disposed) {
      return; // a late message must not reach a disposed DotNetObjectReference
    }
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
