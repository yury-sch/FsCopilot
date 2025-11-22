class FsCopilotHandler {
    constructor(instrument) {
        this.instrument = instrument;
        this.panelId = Math.floor(Math.random() * 100000)
        // Only one panel should fire event
        console.log(`[FsCopilot] Created handler ${this.panelId} for instrument ${this.instrument.instrumentIdentifier}`);
        SimVar.SetSimVarValue('L:FsCopilotHandlerId', 'Number', this.panelId)

        this.network = new FsCopilotNetwork();
        this.events = new HTMLEvents();
        this.watcher = new VarWatcher();
        this.network.addEventListener('message', data => this.onMessage(data))
        this.network.addEventListener('close', () => this.watcher.clear());
        if (instrument.isInteractive) {
            this.network.addEventListener('open', () => this.events.startDocumentListener());
        }
        this.network.addEventListener('close', () => this.events.clear());
        this.watcher.addEventListener('update', ev => {
            if (!this.canProcess()) return;
            this.network.send({
                type: 'var',
                name: ev.name,
                value: ev.value
            });
        });
        this.events.addEventListener('button', ev => {
            this.network.send({
                type: 'button',
                instrument: this.instrument.instrumentIdentifier,
                id: ev.elementId
            });
        });
        this.events.addEventListener('input', ev => {
            this.network.send({
                type: 'input',
                instrument: this.instrument.instrumentIdentifier,
                id: ev.elementId,
                value: ev.value
            });
        });
    }

    canProcess() {
        const isLast = SimVar.GetSimVarValue('L:FsCopilotHandlerId', 'Number') == this.panelId;
        if (!isLast) this.watcher.clear();
        return isLast;
    }

    onMessage(data) {
        if (!this.canProcess()) return;
        
        switch (data.type) {
            case 'watch': {
                this.watcher.watch(data.name, data.units);
                break;
            }
            case 'unwatch': {
                this.watcher.unwatch(data.name);
                break;
            }
            // case 'call': {
            //     if (data.name.startsWith('H:')) this.instrument.onInteractionEvent([data.name.substring(2)]);
            //     break;
            // }
            case 'set': {
                SimVar.SetSimVarValue(data.name, 'number', parseFloat(data.value));
                break;
            }
            case 'button': {
                if (this.instrument.instrumentIdentifier !== data.instrument) break;
                HTMLEvents.setPanel(data.name);
                break;
            }
            case 'input': {
                if (this.instrument.instrumentIdentifier !== data.instrument) break;
                HTMLEvents.setInput(data.name, data.value);
                break;
            }
        }
    }

    interact(name) {
        if (!this.canProcess()) return; // Only one gauge should send interaction button events
        this.network.send({
            type: 'hevent',
            name: name
        });
    }
}
