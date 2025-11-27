class FsCopilotHandler {
    constructor(instrument) {
        this.panelId = Math.floor(Math.random() * 100000)
        // Only one panel should fire event
        console.log(`[FsCopilot] Created handler ${this.panelId} for instrument ${instrument.instrumentIdentifier}`);
        SimVar.SetSimVarValue('L:FsCopilotHandlerId', 'Number', this.panelId)

        this.network = new FsCopilotNetwork();
        this.watcher = new VarWatcher();
        this.network.addEventListener('message', msg => {
            if (!this._canProcess()) return;

            switch (msg.type) {
                case 'watch':
                    this.watcher.watch(msg.name, msg.units);
                    break;
                case 'unwatch':
                    this.watcher.unwatch(msg.name);
                    break;
                case 'set':
                    SimVar.SetSimVarValue(msg.name, 'number', parseFloat(msg.value));
                    break;
                case 'interact':
                    if (instrument.instrumentIdentifier !== msg.instrument) break;
                    HtmlEvents.dispatch(msg.event, msg.id, msg.value);
                    break;
            }
        });
        this.network.addEventListener('close', () => this.watcher.clear());
        this.watcher.addEventListener('update', ev => {
            if (!this._canProcess()) return;
            this.network.send({type: 'var', name: ev.name, value: ev.value});
        });
        
        if (instrument.isInteractive) {
            const events = new HtmlEvents();
            events.addEventListener('emit', ev => {
                this.network.send({type: 'interact', instrument: instrument.instrumentIdentifier, event: ev.type, id: ev.id, value: ev.value});
            });
        }
    }

    _canProcess() {
        const isLast = SimVar.GetSimVarValue('L:FsCopilotHandlerId', 'Number') == this.panelId;
        if (!isLast) this.watcher.clear();
        return isLast;
    }

    interact(name) {
        if (!this._canProcess()) return; // Only one gauge should send interaction button events
        this.network.send({type: 'hevent', name: name});
    }
}
