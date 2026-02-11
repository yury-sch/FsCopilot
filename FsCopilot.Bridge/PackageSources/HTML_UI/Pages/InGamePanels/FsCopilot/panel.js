class FsCopilotPanel extends TemplateElement {
	constructor() {
		super(...arguments);
	}

	connectedCallback() {
		super.connectedCallback();
        console.log('[FsCopilot] Panel connected.');

        const ingameUi = this.querySelector('ingame-ui');
        const tglControl = this.querySelector('#toggleControl');
		if (!ingameUi || !tglControl) return;

        let bus = new Bus();
        tglControl.onclick = () => setTimeout(() => {
            bus.send({'type': 'config', 'control': tglControl.toggled});
        }, 100);

        bus.addEventListener('open', () => bus.send({'type': 'config'}));
        bus.addEventListener('message', msg => {
            switch (msg.type) {
                case 'config':
                    tglControl.setValue(msg.control);
                    break;
            }
        });
        bus.addEventListener('close', () => ingameUi.closePanel());
	}
}

window.customElements.define("ingamepanel-fscopilot", FsCopilotPanel);
checkAutoload();