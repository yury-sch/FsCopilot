const clickEvents = ["click", "mouseup", "mousedown"].map(eventType => {
    let evt = new MouseEvent(eventType, {
        cancelable: true,
        bubbles: true
    })
    evt.FsCopilot = true
    return evt
})
const inputEvent = new Event('input', {
    bubbles: true
});
const nativeInputSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value").set

class HtmlEvents extends Emitter {
    constructor() {
        super();
        this._bindedInputs = {};
        this._documentListenerLoop = null;
        this._mouseListener = (e) => {
            if (e.FsCopilot) return;
            if (e.button !== 0) return; // only left button click

            let currentWorking = e.target
            while (currentWorking != null && currentWorking.id == '') {
                currentWorking = currentWorking.parentNode
            }

            if (!currentWorking) return;
            this.dispatchEvent('button', {elementId: currentWorking.id});
        };
    }

    start() {
        this.stop();
        document.addEventListener('mouseup', this._mouseListener, false);

        // const addElement = this._addElement.bind(this)

        this._documentListenerLoop = setInterval(() => {
            document.querySelectorAll('*').forEach((element) => {
                // addElement(element)
                this._addButton(element);
                this._addInput(element);
            })
        }, 500);
    }

    stop() {
        this._bindedInputs = {};
        if (!!this._documentListenerLoop) {
            clearInterval(this._documentListenerLoop);
            this._documentListenerLoop = null;    
        }
        document.removeEventListener('mouseup', this._mouseListener, false);
    }

    // _addElement(element) {
    //     this._addButton(element)
    //     this._addInput(element)
    // }

    _addButton(el) {
        if (el.id != '') return;

        const getAttributesAsOneString = (element) => {
            let longString = ''

            if (element.hasAttributes()) {
                let attrs = element.attributes;
                for (let i = attrs.length - 1; i >= 0; i--) {
                    longString += attrs[i].name + '#' + attrs[i].value
                }
            }

            return longString
        }

        const getHash = (string) => {
            let hash = 0,
                i, chr;
            for (i = 0; i < string.length; i++) {
                chr = string.charCodeAt(i);
                hash = ((hash << 5) - hash) + chr;
                hash |= 0; // Convert to 32bit integer
            }
            return hash;
        }

        const countParents = (element) => {
            let count = 0
            let workingElement = element;

            while (workingElement != null) {
                count++
                workingElement = workingElement.parentNode
            }

            return count
        }

        const getPositionOfElementInParent = (element) => {
            if (element.parentNode == null) return 0;

            let nodes = element.parentNode.childNodes
            for (let index = 0; index < nodes.length; index++) {
                const otherElement = nodes[index];
                if (otherElement.isEqualNode(element)) return index;
            }

            return 0;
        }

        const generateHTMLHash = (element) => {
            let hash = getHash(getAttributesAsOneString(element))
            hash += countParents(element) * getPositionOfElementInParent(element)
            return hash
        }

        const getIdCorrected = (id) => {
            while (document.getElementById(id) != null) id += 1;
            return id
        }

        el.id = el.id || getIdCorrected(generateHTMLHash(el));
    }

    _addInput(element) {
        if (!(element instanceof HTMLInputElement) || this._bindedInputs[element.id] == true) return;

        this._bindedInputs[element.id] = true

        let cacheValue = null
        element.oninput = () => {
            if (cacheValue == element.value) return;
            cacheValue = element.value
            this.dispatchEvent('input', {elementId: element.id, value: element.value});
        }
    }
    
    static setInput(element, value) {
        element = document.getElementById(element);
        nativeInputSetter.call(element, value);
        element.dispatchEvent(inputEvent);
    }

    static setPanel(buttonName) {
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
