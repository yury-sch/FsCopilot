class FsCopilotHandler {
    constructor(instrument) {
        // Only one panel should fire event
        this.panelId = Math.floor(Math.random() * 100000);
        console.log(`[FsCopilot] Created handler ${this.panelId} for instrument ${instrument.instrumentIdentifier}`);
        SimVar.SetSimVarValue('L:FSC_HANDLER', 'number', this.panelId);

        this.bus = new Bus();
        this.bus.addEventListener('message', msg => {
            if (!this._canProcess()) return;

            switch (msg.type) {
                case 'interact':
                    if (instrument.instrumentIdentifier !== msg.instrument) break;
                    HtmlEvents.dispatch(msg.event, msg.id, msg.value);
                    break;
            }
        });
        if (instrument.isInteractive) {
            const events = new HtmlEvents();
            events.addEventListener('emit', ev => {
                this.bus.send({type: 'interact', instrument: instrument.instrumentIdentifier, event: ev.type, id: ev.id, value: ev.value});
            });
        }
    }

    _canProcess() {
        return SimVar.GetSimVarValue('L:FSC_HANDLER', 'number') == this.panelId;
    }

    interact(name) {
        if (!this._canProcess()) return; // Only one gauge should send interaction button events
        this.bus.send({type: 'hevent', name: name});
    }
}
