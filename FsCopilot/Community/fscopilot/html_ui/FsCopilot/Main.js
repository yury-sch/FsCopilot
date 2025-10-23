var handler = null

class FsCopilotHandler {
    constructor(instrument) {
        this.instrument = instrument;
        this.instrumentName = instrument.instrumentIdentifier;
        this.isInteractive = instrument.isInteractive;

        this.net = new FsCopilotNetwork(this.onMessage.bind(this), this.onConnected.bind(this), this.onDisconnected.bind(this), () => this.canProcess() || this.isInteractive)
        this.events = new FsCopilotHTMLEvents(this.onButton.bind(this), this.onInput.bind(this))

        this.panelId = Math.floor(Math.random() * 100000)
        // Only one panel should fire event
        SimVar.SetSimVarValue('L:FsCopilotPanelId', 'Number', this.panelId)

        this.start()
    }

    canProcess() {
        return SimVar.GetSimVarValue('L:FsCopilotPanelId', 'Number') == this.panelId
    }

    start() {
        setTimeout(this.startCall.bind(this), 3000)
    }

    startCall() {
        this.net.startAttemptConnection(this.instrumentName)
    }

    onMessage(data) {
        switch (data.type) {
            case 'input': {
                this.events.setInput(document.getElementById(data.id), data.value)
                break;
            }
            case 'interaction': {
                if (this.canProcess())
                {
                    this.instrument.onInteractionEvent([data.key])
                }
                break;
            }
            case 'button': {
                break;
            }
        }
    }

    onConnected() {
        if (this.isInteractive) {
            this.events.bindEvents()
            this.events.startDocumentListener()
        }
    }

    onDisconnected() {
        this.events.clear()
    }

    onInput(elementId, value) {
        console.log(`${this.instrumentName} (onButton) ${elementId} : ${value}`);
        this.net.sendInputEvent(elementId, value)
    }

    onButton(elementId) {
        console.log(`${this.instrumentName} (onButton) ${elementId}`);
        this.net.sendButtonEvent(elementId)
    }

    onInteraction(args) {
        if (this.canProcess()) { // Only one gauge should send interaction button events
            console.log(`${this.instrumentName} (onInteraction) ${args}`);
            this.net.sendInteractionEvent(args[0]);
        }
        // // Panel event
        // if (args[0].startsWith('YCB')) {
        //     this.events.setPanel(args[0].substring(3), this.instrumentName)
        //     return false
        // } else if (args[0].startsWith('YCH')) {
        //     args[0] = args[0].substring(3)
        // } else if (this.canProcess()) { // Only one gauge should send interaction button events
        //     this.net.sendInteractionEvent('H:YCH' + args[0]);
        // }
        return true
    }
}

class FsCopilotNetwork {
    constructor(onMessageCallback, connectedCallback, disconnectedCallback, canConnect) {
        this.socket = null;
        this.socketConnected = false;
        this.instrumentName = ''

        this.onMessageCallback = onMessageCallback ? onMessageCallback : () => { };
        this.connectedCallback = connectedCallback ? connectedCallback : () => { };
        this.disconnectedCallback = disconnectedCallback ? disconnectedCallback : () => { };
        this.canConnect = canConnect ? canConnect : () => false;
    }

    connectWebsocket() {
        if (this.socket !== null) this.socket.close();
        
        this.socket = new WebSocket('ws://localhost:8870/bridge/');
        this.socket.addEventListener('open', this.onConnected.bind(this));
        this.socket.addEventListener('close', this.onConnectionLost.bind(this));
        this.socket.addEventListener('error', this.onConnectionError.bind(this));
        this.socket.addEventListener('message', this.onSocketMessage.bind(this));
    }

    onConnectionLost() {
        delete this.socket
        this.socket = null
        this.socketConnected = false
        this.disconnectedCallback()
    }

    onConnectionError() {
        this.socket.close()
    }

    // isFsCopilotRunning() {
    //     return SimVar.GetSimVarValue('L:FsCopilotStarted', 'Boolean') == true
    // }

    startAttemptConnection(instrumentName) {
        this.instrumentName = instrumentName
        setInterval(this.attemptConnection.bind(this), 4000)
    }

    attemptConnection() {
        // if (this.socketConnected || !this.canConnect() || !this.isFsCopilotRunning()) {
        //     return
        // }
        if (this.socketConnected || !this.canConnect()) return;
        this.connectWebsocket()
    }

    sendObjectAsJSON(message) {
        if (this.socket === null || this.socket.readyState != 1) return
        this.socket.send(JSON.stringify(message))
    }

    sendInteractionEvent(eventName) {
        this.sendObjectAsJSON({
            instrument: this.instrumentName,
            type: 'interaction',
            key: eventName
        });
    }

    sendButtonEvent(eventName) {
        this.sendObjectAsJSON({
            instrument: this.instrumentName,
            type: 'button',
            key: eventName
        });
    }

    sendInputEvent(elementId, value) {
        this.sendObjectAsJSON({
            instrument: this.instrumentName,
            type: 'input',
            key: elementId,
            value: value
        })
    }

    onConnected() {
        console.log('FsCopilot websocket connected.')
        this.socketConnected = true
        this.connectedCallback()
    }

    onSocketMessage(event) {
        let data = JSON.parse(event.data)
        this.onMessageCallback(data)
    }
}

const clickEvents = ['click', 'mouseup', 'mousedown'].map(eventType => {
    let evt = new MouseEvent(eventType, {
        cancelable: true,
        bubbles: true
    })
    evt.FsCopilot = true
    return evt
})
const inputEvent = new Event('input', { bubbles: true });
const nativeInputSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set

class FsCopilotHTMLEvents {
    constructor(buttonCallback, inputCallback) {
        this.bindedInputs = {}

        this.buttonCallback = buttonCallback
        this.inputCallback = inputCallback
        this.documentListenerLoop = null
        this.documentMouseListener = null
    }

    bindEvents() {
        this.stopMouseUpListener()

        this.documentMouseListener = document.addEventListener('mouseup', (e) => {
            if (e.FsCopilot) return;

            let currentWorking = e.target

            while (currentWorking.id == '' && currentWorking != null) {
                currentWorking = currentWorking.parentNode
            }

            if (!currentWorking) return;

            this.buttonCallback(currentWorking.id)
        })
    }

    stopMouseUpListener() {
        if (this.documentMouseListener === null) return;
        document.removeEventListener(this.documentMouseListener)
        this.documentMouseListener = null
    }

    startDocumentListener() {
        this.stopDocumentListener()

        const addElement = this.addElement.bind(this)

        this.documentListenerLoop = setInterval(() => {
            document.querySelectorAll('*').forEach((element) => {
                addElement(element)
            })
        }, 500)
    }

    stopDocumentListener() {
        if (this.documentListenerLoop === null) return;
        clearInterval(documentListenerLoop)
        this.documentListenerLoop = null
    }

    addElement(element) {
        this.addButton(element)
        this.addInput(element)
    }

    getHash(string) {
        let hash = 0,
            i, chr;
        for (i = 0; i < string.length; i++) {
            chr = string.charCodeAt(i);
            hash = ((hash << 5) - hash) + chr;
            hash |= 0; // Convert to 32bit integer
        }
        return hash;
    }

    getPositionOfElementInParent(element) {
        if (element.parentNode == null) return 0;

        let nodes = element.parentNode.childNodes
        for (let index = 0; index < nodes.length; index++) {
            const otherElement = nodes[index];
            if (otherElement.isEqualNode(element)) return index;
        }

        return 0;
    }

    countParents(element) {
        let count = 0
        let workingElement = element;

        while (workingElement != null) {
            count++
            workingElement = workingElement.parentNode
        }

        return count
    }

    getAttributesAsOneString(element) {
        let longString = ''

        if (element.hasAttributes()) {
            let attrs = element.attributes;
            for (let i = attrs.length - 1; i >= 0; i--) {
                longString += attrs[i].name + '#' + attrs[i].value
            }
        }

        return longString
    }

    generateHTMLHash(element) {
        let hash = this.getHash(this.getAttributesAsOneString(element))
        hash += this.countParents(element) * this.getPositionOfElementInParent(element)
        return hash
    }

    getIdCorrected(id) {
        while (document.getElementById(id) != null) id += 1;
        return id
    }

    addButton(element) {
        if (element.id != '') return;

        let id = element.id || this.getIdCorrected(this.generateHTMLHash(element))
        element.id = id
    }

    addInput(element) {
        if (!(element instanceof HTMLInputElement) || this.bindedInputs[element.id] == true) return;

        this.bindedInputs[element.id] = true

        let cacheValue = null
        element.oninput = () => {
            if (cacheValue == element.value) return;
            cacheValue = element.value
            // SEND VALUE
            this.inputCallback(element.id, element.value)
        }
    }

    clear() {
        this.bindedInputs = {}
        this.stopDocumentListener()
        this.stopMouseUpListener()
    }

    setInput(element, value) {
        nativeInputSetter.call(element, value);
        element.dispatchEvent(inputEvent);
    }

    setPanel(eventName, instrumentName) {
        const split = eventName.indexOf('#')
        const targetInstrumentName = eventName.substring(0, split)

        if (targetInstrumentName != instrumentName) {
            return
        }

        const buttonName = eventName.substring(split + 1)
        const button = document.getElementById(buttonName)

        clickEvents.forEach(evt => {
            button.dispatchEvent(evt)
        });
    }

    // static processInput(element, value) {
    //     FsCopilotHTMLTrigger.nativeInputSetter.call(element, value);
    //     element.dispatchEvent(inputEvent);
    // }
}
