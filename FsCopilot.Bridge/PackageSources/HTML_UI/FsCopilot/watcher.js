class VarWatcher extends Emitter {
    constructor({ hz = 6, epsilon = 1e-6 } = {}) {
        super();
        this._interval = 1000 / hz;
        this._epsilon = epsilon;
        this._vars = new Map();
    }

    watch(name, units = 'number') {
        const wasEmpty = this._vars.size === 0;
        this._vars.set(name, { units, lastValue: null });
        if (wasEmpty) this._poll();
    }

    unwatch(name) {
        this._vars.delete(name);
    }

    clear() {
        this._vars.clear();
    }

    _poll() {
        if (this._vars.size === 0) return;

        for (const [name, entry] of this._vars.entries()) {
            const newValue = SimVar.GetSimVarValue(name, entry.units);
            if (this._equals(entry.lastValue, newValue)) continue;

            entry.lastValue = newValue;
            this.dispatchEvent('update', {name, value: newValue});
        }

        setTimeout(() => this._poll(), this._interval);
    }

    _equals(a, b) {
        if (a == null && b != null) return false;
        if (typeof b === 'number') return !Number.isNaN(b) && Math.abs(b - a) <= this._epsilon;
        return a === b;
    }
}