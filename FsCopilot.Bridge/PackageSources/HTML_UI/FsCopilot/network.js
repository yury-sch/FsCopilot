class FsCopilotNetwork extends Emitter {
    constructor() {
        super();
        this._ws = null;
        this.connected = false;
        setTimeout(() => this._connect(), 1000);
    }

    _connect() {
        if (this._ws != null && 
            (this._ws.readyState === WebSocket.OPEN || 
             this._ws.readyState === WebSocket.CONNECTING)) return;

        this._ws = new WebSocket('ws://localhost:8870/bridge/');
        this._ws.addEventListener('open', () => {
            console.log('[FsCopilot] [Network] Socket connected.');
            this.connected = true;
            this.dispatchEvent('open', {});
        });
        this._ws.addEventListener('close', () => {
            this.connected = false;
            this.dispatchEvent('close', {});
            setTimeout(() => this._connect(), 1000);
        });
        this._ws.addEventListener('error', () => {
            try { this._ws.close(); } catch (e) { /* ignore */ }
        });
        this._ws.addEventListener('message', (event) => {
            console.log(`[FsCopilot] [Network] (recv) ${event.data}`);
            let data;
            try {
                data = JSON.parse(event.data);
            } catch (e) {
                console.error('[FsCopilot] [Network] (recv) Invalid JSON', e);
            }
            this.dispatchEvent('message', data);
        });
    }

    send(data) {
        if (this._ws === null || this._ws.readyState !== WebSocket.OPEN) return;
        let message = JSON.stringify(data);
        this._ws.send(message);
        console.log(`[FsCopilot] (send) ${message}`);
    }
}