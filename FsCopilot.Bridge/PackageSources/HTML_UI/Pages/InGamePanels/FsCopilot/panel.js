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
        
        tglControl.onclick = () => setTimeout(() => {
            network.send({'type': 'config', 'control': tglControl.toggled});
        }, 100);
        
        let network = new FsCopilotNetwork();
        network.addEventListener('open', () => network.send({'type': 'config'}));
        network.addEventListener('message', msg => {
            switch (msg.type) {
                case 'config':
                    tglControl.setValue(msg.control);
                    break;
            }
        });
        network.addEventListener('close', () => ingameUi.closePanel());
	}
}

window.customElements.define("ingamepanel-fscopilot", FsCopilotPanel);
checkAutoload();