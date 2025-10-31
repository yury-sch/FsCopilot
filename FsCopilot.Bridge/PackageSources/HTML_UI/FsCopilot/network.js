class FsCopilotNetwork {
    constructor() {
        this.ws = null;
        this.wsConnected = false;
        this.connectionHandlers = new Set();
        this.disconnectionHandlers = new Set();
        this.messageHandlers = new Set();
        this.connect();
    }

    connect() {
        if (this.wsConnected) return;

        this.ws = new WebSocket('ws://localhost:8870/bridge/');
        this.ws.addEventListener('open', () => {
            console.log('[FsCopilot] [Network] Socket connected.');
            this.wsConnected = true;
            this.connectionHandlers.forEach(h => {
                try { h(); } catch (e) { console.error(e); }
            });
        });
        this.ws.addEventListener('close', () => {
            this.wsConnected = false;
            delete this.ws;
            this.ws = null;
            this.disconnectionHandlers.forEach(h => {
                try { h(); } catch (e) { console.error(e); }
            });
            setTimeout(() => {
                this.connect();
            }, 100);
        });
        this.ws.addEventListener('error', () => {
            this.ws.close();
        });
        this.ws.addEventListener('message', (event) => {
            console.log(`[FsCopilot] [Network] (recv) ${event.data}`);
            let data = JSON.parse(event.data);
            this.messageHandlers.forEach(h => {
                try { h(data); } catch (e) { console.error(e); }
            });
        });
    }

    send(data) {
        if (this.ws === null || this.ws.readyState != 1) return
        let message = JSON.stringify(data)
        console.log(`[FsCopilot] (send) ${message}`);
        this.ws.send(message)
    }

    onConnected(callback) {
        this.connectionHandlers.add(callback);
        if (this.wsConnected) callback();
        return () => {
            this.connectionHandlers.delete(callback);
        };
    }

    onDisconnected(callback) {
        this.disconnectionHandlers.add(callback);
        if (!this.wsConnected) callback();
        return () => {
            this.disconnectionHandlers.delete(callback);
        };
    }

    onMessage(callback) {
        this.messageHandlers.add(callback);
        return () => {
            this.messageHandlers.delete(callback);
        };
    }
}