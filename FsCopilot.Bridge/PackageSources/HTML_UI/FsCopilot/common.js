class Emitter {
    constructor() { this._handlers = new Map(); } // event -> Set<fn>
    addEventListener(type, fn) {
        let set = this._handlers.get(type);
        if (!set) this._handlers.set(type, (set = new Set()));
        set.add(fn);
    }
    removeEventListener(type, fn) {
        const set = this._handlers.get(type);
        if (set) set.delete(fn);
    }
    dispatchEvent(type, evt) {
        const set = this._handlers.get(type);
        if (!set) return;
        for (const fn of set) {
            try { fn(evt); } catch (e) { console.error(`[Emitter] listener error for ${type}`, e); }
        }
    }
}