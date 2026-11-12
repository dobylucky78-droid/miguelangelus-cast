// relógio num Worker: a página do plugin fica escondida e o navegador atrasaria um setInterval comum
self.onmessage = e => setInterval(() => self.postMessage(0), e.data);
