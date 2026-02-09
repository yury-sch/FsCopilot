class FsCopilotHandler {
    constructor(instrument) {
        // Only one panel should fire event
        this.panelId = Math.floor(Math.random() * 100000);
        console.log(`[FsCopilot] Created handler ${this.panelId} for instrument ${instrument.instrumentIdentifier}`);
        SimVar.SetSimVarValue('L:FSC_HANDLER', 'Number', this.panelId);

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
            if (!this._canProcess()) {
                this.watcher.clear();
                return;
            }
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
        return SimVar.GetSimVarValue('L:FSC_HANDLER', 'Number') == this.panelId;
    }

    interact(name) {
        if (!this._canProcess()) return; // Only one gauge should send interaction button events
        this.network.send({type: 'hevent', name: name});
    }
}
