class Bus extends Emitter {
    constructor() {
        super();

        this.CommBusListener = RegisterCommBusListener(() => {
            console.log("[FsCopilot] [Bus] Register");
        });
        this.CommBusListener.on("FSC_CLIENT_EVENT", (dataStr) => {
            console.log(`[FsCopilot] [Bus] (recv) ${dataStr}`);
            let data;
            try {
                data = JSON.parse(dataStr);
            } catch (e) {
                console.error('[FsCopilot] [Bus] (recv) Invalid JSON', e);
            }
            this.dispatchEvent('message', data);
        });
    }

    send(data) {
        let msg = JSON.stringify(data);
        this.CommBusListener.callWasm("FSC_GAUGE_EVENT", msg);
        console.log(`[FsCopilot] [Bus] (send) ${msg}`);
    }
}