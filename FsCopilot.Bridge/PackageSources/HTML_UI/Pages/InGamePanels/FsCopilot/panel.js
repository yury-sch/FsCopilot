class IngamePanelCustom extends TemplateElement {
	constructor() {
		super(...arguments);

	}

	connectedCallback() {
		super.connectedCallback();

		this.ingameUi = this.querySelector('ingame-ui');

		if (this.ingameUi) {

			//this.ingameUi.addEventListener("panelActive", (e) => {
			// Panel became active
			//});

			//this.ingameUi.addEventListener("panelInactive", (e) => {
			// Panel became inactive
			//});

			// Other events:
			//ToggleExternPanel
			//rectUpdate
			//OnResize
		}

	}
}
//
window.customElements.define("ingamepanel-custom", IngamePanelCustom);
checkAutoload();