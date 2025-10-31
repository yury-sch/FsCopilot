class FsCopilotHandler {
    constructor(instrument) {
        this.instrument = instrument;
        this.panelId = Math.floor(Math.random() * 100000)
        // Only one panel should fire event
        SimVar.SetSimVarValue('L:FsCopilotHandlerId', 'Number', this.panelId)

        // this.events = new FsCopilotHTMLEvents(this.onButton.bind(this), this.onInput.bind(this))
        this.network = new FsCopilotNetwork();
        this.network.onMessage(this.onMessage.bind(this))
        // this.network.onConnected(() => {
        //     if (instrument.isInteractive) {
        //         this.events.bindEvents()
        //         this.events.startDocumentListener()
        //     }
        // })
        // this.network.onDisconnected(() => {
        //     this.events.clear()
        // })
    }

    canProcess() {
        return SimVar.GetSimVarValue('L:FsCopilotHandlerId', 'Number') == this.panelId;
    }

    onMessage(data) {
        if (!this.canProcess()) return;
        
        switch (data.type) {
            // case 'input': {
            //     FsCopilotHTMLTrigger.setInput(document.getElementById(data.key), data.value);
            //     break;
            // }
            case 'call': {
                if (data.key.startsWith('H:')) this.instrument.onInteractionEvent([data.key.substring(2)]);
                break;
            }
            case 'set': {
                SimVar.SetSimVarValue(data.key, 'number', parseFloat(data.value));
                break;
            }
            // case 'button': {
            //     FsCopilotHTMLTrigger.setPanel(data.key, data.instrument);
            //     break;
            // }
        }
    }

    interact(name) {
        if (this.canProcess()) return; // Only one gauge should send interaction button events
        this.network.send({
            type: 'hevent',
            key: name
        });
    }

    // onInput(elementId, value) {
    //     this.network.send({
    //         instrument: this.instrument.instrumentIdentifier,
    //         type: 'input',
    //         key: elementId,
    //         value: value
    //     })
    // }
    //
    // onButton(elementId) {
    //     this.network.send({
    //         instrument: this.instrument.instrumentIdentifier,
    //         type: 'button',
    //         key: elementId
    //     });
    // }
}

// class FsCopilotHTMLEvents {
//     constructor(buttonCallback, inputCallback) {
//         this.bindedInputs = {}
//
//         this.buttonCallback = buttonCallback
//         this.inputCallback = inputCallback
//         this.documentListenerLoop = null
//         this.documentMouseListener = null
//     }
//
//     bindEvents() {
//         this.stopMouseUpListener()
//
//         this.documentMouseListener = document.addEventListener('mouseup', (e) => {
//             if (e.FsCopilot) return;
//
//             let currentWorking = e.target
//
//             while (currentWorking.id == '' && currentWorking != null) {
//                 currentWorking = currentWorking.parentNode
//             }
//
//             if (!currentWorking) return;
//
//             this.buttonCallback(currentWorking.id)
//         })
//     }
//
//     stopMouseUpListener() {
//         if (this.documentMouseListener === null) return;
//         document.removeEventListener(this.documentMouseListener)
//         this.documentMouseListener = null
//     }
//
//     startDocumentListener() {
//         this.stopDocumentListener()
//
//         const addElement = this.addElement.bind(this)
//
//         this.documentListenerLoop = setInterval(() => {
//             document.querySelectorAll('*').forEach((element) => {
//                 addElement(element)
//             })
//         }, 500)
//     }
//
//     stopDocumentListener() {
//         if (this.documentListenerLoop === null) return;
//         clearInterval(documentListenerLoop)
//         this.documentListenerLoop = null
//     }
//
//     addElement(element) {
//         this.addButton(element)
//         this.addInput(element)
//     }
//
//     getHash(string) {
//         let hash = 0,
//             i, chr;
//         for (i = 0; i < string.length; i++) {
//             chr = string.charCodeAt(i);
//             hash = ((hash << 5) - hash) + chr;
//             hash |= 0; // Convert to 32bit integer
//         }
//         return hash;
//     }
//
//     getPositionOfElementInParent(element) {
//         if (element.parentNode == null) return 0;
//
//         let nodes = element.parentNode.childNodes
//         for (let index = 0; index < nodes.length; index++) {
//             const otherElement = nodes[index];
//             if (otherElement.isEqualNode(element)) return index;
//         }
//
//         return 0;
//     }
//
//     countParents(element) {
//         let count = 0
//         let workingElement = element;
//
//         while (workingElement != null) {
//             count++
//             workingElement = workingElement.parentNode
//         }
//
//         return count
//     }
//
//     getAttributesAsOneString(element) {
//         let longString = ''
//
//         if (element.hasAttributes()) {
//             let attrs = element.attributes;
//             for (let i = attrs.length - 1; i >= 0; i--) {
//                 longString += attrs[i].name + '#' + attrs[i].value
//             }
//         }
//
//         return longString
//     }
//
//     generateHTMLHash(element) {
//         let hash = this.getHash(this.getAttributesAsOneString(element))
//         hash += this.countParents(element) * this.getPositionOfElementInParent(element)
//         return hash
//     }
//
//     getIdCorrected(id) {
//         while (document.getElementById(id) != null) id += 1;
//         return id
//     }
//
//     addButton(element) {
//         if (element.id != '') return;
//
//         let id = element.id || this.getIdCorrected(this.generateHTMLHash(element))
//         element.id = id
//     }
//
//     addInput(element) {
//         if (!(element instanceof HTMLInputElement) || this.bindedInputs[element.id] == true) return;
//
//         this.bindedInputs[element.id] = true
//
//         let cacheValue = null
//         element.oninput = () => {
//             if (cacheValue == element.value) return;
//             cacheValue = element.value
//             // SEND VALUE
//             this.inputCallback(element.id, element.value)
//         }
//     }
//
//     clear() {
//         this.bindedInputs = {}
//         this.stopDocumentListener()
//         this.stopMouseUpListener()
//     }
// }
//
// const clickEvents = ["click", "mouseup", "mousedown"].map(eventType => {
//     let evt = new MouseEvent(eventType, {
//         cancelable: true,
//         bubbles: true
//     })
//     evt.FsCopilot = true
//     return evt
// })
// const inputEvent = new Event('input', {
//     bubbles: true
// });
// const nativeInputSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value").set
//
// class FsCopilotHTMLTrigger {
//     static setInput(element, value) {
//         nativeInputSetter.call(element, value);
//         element.dispatchEvent(inputEvent);
//     }
//
//     static setPanel(eventName, instrumentName) {
//         const split = eventName.indexOf("#")
//         const targetInstrumentName = eventName.substring(0, split)
//
//         if (targetInstrumentName != instrumentName) {
//             return
//         }
//
//         const buttonName = eventName.substring(split + 1)
//         const button = document.getElementById(buttonName)
//
//         clickEvents.forEach(evt => {
//             button.dispatchEvent(evt)
//         });
//     }
//
//     static processInput(element, value) {
//         FsCopilotHTMLTrigger.nativeInputSetter.call(element, value);
//         element.dispatchEvent(inputEvent);
//     }
// }
