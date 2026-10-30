/**
 * Web client for the TWAIN scanning demo.
 *
 * - Loads Nutrient Web SDK from the CDN (global window.NutrientViewer, see index.html).
 * - Calls the .NET TWAIN service, shows its Server-Sent Events progress,
 *   and opens the scanned PDF in the viewer.
 */

// Optional settings from the repository .env file (see vite.config.js).
// No license key = evaluation mode (watermark).
const LICENSE_KEY = import.meta.env.VITE_NUTRIENT_WEB_LICENSE_KEY || "";
const TWAIN_API_URL = import.meta.env.VITE_TWAIN_API_URL || "http://localhost:5000";

const NutrientViewer = window.NutrientViewer;

let currentInstance = null;

// Show an empty state with a scan button until the first scan
window.addEventListener("DOMContentLoaded", () => {
  if (!NutrientViewer) {
    console.error("NutrientViewer not found. Check that the Web SDK script in index.html loads.");
    return;
  }

  const appContainer = document.querySelector('.App');
  appContainer.innerHTML = `
    <div style="display: flex; flex-direction: column; align-items: center; justify-content: center; height: 100vh; background: #1a1a1a; color: #ecf0f1; font-family: system-ui, -apple-system, sans-serif;">
      <svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="margin-bottom: 24px; opacity: 0.6;">
        <path d="M3 7V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v2"/>
        <path d="M3 17v2a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-2"/>
        <path d="M3 12h18"/>
        <circle cx="8" cy="12" r="1"/>
        <circle cx="12" cy="12" r="1"/>
        <circle cx="16" cy="12" r="1"/>
      </svg>
      <h2 style="margin: 0 0 12px 0; font-size: 24px; font-weight: 600;">Ready to Scan</h2>
      <p style="margin: 0 0 32px 0; font-size: 16px; opacity: 0.7;">Click the button below to scan a document</p>
      <button id="scan-btn" style="padding: 12px 32px; background: #3498db; color: white; border: none; border-radius: 8px; font-size: 16px; font-weight: 600; cursor: pointer; transition: background 0.2s;">
        Scan Document
      </button>
    </div>
  `;

  document.getElementById('scan-btn').addEventListener('click', scan);
});

// Function to add scan button to toolbar
function addScanButton(instance) {
  let toolbarItems = [...NutrientViewer.defaultToolbarItems];
  toolbarItems.push({
    type: "custom",
    id: "scan-button",
    title: "Scan Document",
    icon: `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M3 7V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v2"/>
      <path d="M3 17v2a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-2"/>
      <path d="M3 12h18"/>
      <circle cx="8" cy="12" r="1"/>
      <circle cx="12" cy="12" r="1"/>
      <circle cx="16" cy="12" r="1"/>
    </svg>`,
    onPress: function () {
      scan();
    },
  });
  instance.setToolbarItems(toolbarItems);
}


// ------- GdPicture TWAIN Scan via .NET API -------

const scan = async () => {
    try {
        console.log("Calling GdPicture TWAIN API...");

        // Show progress UI
        showProgressUI();

        const response = await fetch(`${TWAIN_API_URL}/api/scan`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            }
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        // Read the Server-Sent Events stream
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            buffer += decoder.decode(value, { stream: true });

            // Process complete messages (separated by \n\n)
            const messages = buffer.split('\n\n');
            buffer = messages.pop(); // Keep incomplete message in buffer

            for (const message of messages) {
                if (message.startsWith('data: ')) {
                    const data = JSON.parse(message.substring(6));
                    console.log('Progress:', data.stage, data.message);

                    updateProgressUI(data);

                    // If complete, load the PDF
                    if (data.stage === 'complete' && data.base64) {
                        await loadScannedPdfFromBase64(data.base64, data.fileName || 'scanned-document.pdf');
                        hideProgressUI();
                    } else if (data.stage === 'error') {
                        hideProgressUI();
                        alert('Scan failed: ' + data.message);
                    }
                }
            }
        }
    } catch (error) {
        console.error("Error calling TWAIN API:", error);
        hideProgressUI();
        alert(`Error connecting to TWAIN service. Please make sure the .NET service is running on ${TWAIN_API_URL}`);
    }
};

// Show progress UI overlay
function showProgressUI() {
    const overlay = document.createElement('div');
    overlay.id = 'progress-overlay';
    overlay.style.cssText = `
        position: fixed;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        background: rgba(0, 0, 0, 0.85);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 10000;
    `;

    overlay.innerHTML = `
        <div style="background: #2c3e50; border-radius: 12px; padding: 32px; width: 500px; max-width: 90%; box-shadow: 0 10px 40px rgba(0,0,0,0.3);">
            <h3 style="margin: 0 0 24px 0; color: #ecf0f1; font-size: 20px; font-weight: 600; text-align: center;">Scanning Document</h3>
            <div style="background: #34495e; border-radius: 8px; height: 12px; overflow: hidden; margin-bottom: 16px;">
                <div id="progress-bar" style="background: linear-gradient(90deg, #3498db, #2ecc71); height: 100%; width: 0%; transition: width 0.3s ease;"></div>
            </div>
            <div id="progress-message" style="color: #bdc3c7; font-size: 14px; text-align: center; min-height: 20px;"></div>
        </div>
    `;

    document.body.appendChild(overlay);
}

// Update progress UI with current status
function updateProgressUI(data) {
    const progressBar = document.getElementById('progress-bar');
    const progressMessage = document.getElementById('progress-message');

    if (progressMessage) {
        progressMessage.textContent = data.message;
    }

    // Calculate progress percentage based on stage
    let percentage = 0;
    switch (data.stage) {
        case 'init':
            percentage = 5;
            break;
        case 'scanning':
            percentage = 20 + (data.current * 30); // 20-50%
            break;
        case 'ocr':
            percentage = 50 + (data.current * 20); // 50-70%
            break;
        case 'saving':
            percentage = 80;
            break;
        case 'reading':
            percentage = 90;
            break;
        case 'complete':
            percentage = 100;
            break;
    }

    if (progressBar) {
        progressBar.style.width = `${Math.min(percentage, 100)}%`;
    }
}

// Hide progress UI
function hideProgressUI() {
    const overlay = document.getElementById('progress-overlay');
    if (overlay) {
        overlay.remove();
    }
}

const loadScannedPdfFromBase64 = async (base64, fileName) => {
    try {
        console.log(`Loading scanned document: ${fileName}`);

        // Decode Base64 to binary
        const byteCharacters = atob(base64);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
            byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const arrayBuffer = byteArray.buffer;

        // Unload existing instance if any
        if (currentInstance) {
            NutrientViewer.unload(".App");
            currentInstance = null;
        }

        // Clear the container (removes the empty state)
        const appContainer = document.querySelector('.App');
        appContainer.innerHTML = '';

        const configuration = {
            container: ".App",
            document: arrayBuffer,
            theme: NutrientViewer.Theme.DARK,
            toolbarItems: [...NutrientViewer.defaultToolbarItems],
        };
        if (LICENSE_KEY) {
            configuration.licenseKey = LICENSE_KEY;
        }

        // Load the ArrayBuffer into Nutrient Viewer
        currentInstance = await NutrientViewer.load(configuration);

        addScanButton(currentInstance);

        console.log("Scanned document loaded successfully");
    } catch (error) {
        console.error("Error loading scanned PDF:", error);
        alert("Error loading PDF into viewer: " + error.message);
    }
};
