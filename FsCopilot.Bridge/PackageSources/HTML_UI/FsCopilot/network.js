class FsCopilotNetwork extends Emitter {
    constructor() {
        super();
        this._ws = null;
        this.connected = false;
        this._connect();
    }

    _connect() {
        if (this.connected) return;

        this._ws = new WebSocket('ws://localhost:8870/bridge/');
        this._ws.addEventListener('open', () => {
            console.log('[FsCopilot] [Network] Socket connected.');
            this.connected = true;
            this.dispatchEvent('open', {});
        });
        this._ws.addEventListener('close', () => {
            this.connected = false;
            delete this._ws;
            this._ws = null;
            this.dispatchEvent('close', {});
            setTimeout(() => this._connect(), 100);
        });
        this._ws.addEventListener('error', () => {
            this._ws.close();
        });
        this._ws.addEventListener('message', (event) => {
            console.log(`[FsCopilot] [Network] (recv) ${event.data}`);
            let data = JSON.parse(event.data);
            this.dispatchEvent('message', data);
        });
    }

    send(data) {
        if (this._ws === null || this._ws.readyState != 1) return
        let message = JSON.stringify(data)
        console.log(`[FsCopilot] (send) ${message}`);
        this._ws.send(message)
    }
}