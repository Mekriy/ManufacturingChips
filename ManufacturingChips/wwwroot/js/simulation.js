"use strict";

// —————— GLOBAL STATE ——————
let shiftTimer    = null;
let shiftEndTime  = null;
let spawnStop     = false;

// —————— CLASS FOR LINE ANIMATION ——————
class LineAnimator {
    constructor(lineIdx) {
        this.lineIdx = lineIdx;
    }

    get lineElem() {
        return document.querySelectorAll('.line')[this.lineIdx];
    }

    get centers() {
        const machines = Array.from(this.lineElem.querySelectorAll('.machine'));
        const rect     = this.lineElem.getBoundingClientRect();
        return machines.map(m => {
            const r = m.getBoundingClientRect();
            return r.left + r.width/2 - rect.left;
        });
    }

    animateArrival(chipId) {
        if (spawnStop) return;
        const queueBox = this.lineElem.querySelector('.queue');
        const countDom = queueBox.querySelector('.queue-count');
        const chip     = document.createElement('div');
        chip.className      = 'chip';
        chip.dataset.chipId = chipId;
        queueBox.appendChild(chip);
        countDom.textContent = queueBox.querySelectorAll('.chip').length;
        logEvent(`Лінія ${this.lineIdx+1}: enqueue (chip ${chipId})`);
    }

    animateQueueToService(chipId) {
        if (spawnStop) return;
        const queueBox = this.lineElem.querySelector('.queue');
        const countDom = queueBox.querySelector('.queue-count');
        const chip = queueBox.querySelector(`.chip[data-chip-id="${chipId}"]`);
        if (chip) {
            chip.remove();
            countDom.textContent = queueBox.querySelectorAll('.chip').length;
        }
        logEvent(`Лінія ${this.lineIdx+1}: dequeue → M1 (chip ${chipId})`);

        const product = document.createElement('div');
        product.className      = 'product';
        product.dataset.chipId = chipId;
        product.style.position = 'absolute';
        product.style.left     = `${this.centers[0]}px`;
        this.lineElem.appendChild(product);
    }

    animateTransfer(chipId, fromMachine, toMachine) {
        if (spawnStop) return;
        const product = this.lineElem.querySelector(`.product[data-chip-id="${chipId}"]`);
        if (!product) return;

        let targetX;
        if (toMachine >= this.centers.length) {
            const rect = this.lineElem.getBoundingClientRect();
            targetX = rect.width + 20;
        } else {
            targetX = this.centers[toMachine];
        }

        product.style.transition = 'left 0.5s ease-in-out, opacity 0.5s';
        product.style.left       = `${targetX}px`;

        if (toMachine >= this.centers.length) {
            product.style.opacity = '0';
            product.addEventListener('transitionend', () => {
                if (product.parentElement) product.remove();
                logEvent(`Лінія ${this.lineIdx+1}: completion (chip ${chipId})`);
            }, { once: true });
        } else {
            logEvent(`Лінія ${this.lineIdx+1}: transfer ${fromMachine+1}→${toMachine+1} (chip ${chipId})`);
        }
    }
}

// —————— SETUP ANIMATORS ——————
const animators = [];
function setupAnimators() {
    animators.length = 0;
    document.querySelectorAll('.line').forEach((_, idx) => {
        animators.push(new LineAnimator(idx));
    });
}

// —————— TIMER & STATS (unchanged) ——————
function logEvent(msg) {
    const ul   = document.getElementById("logList");
    const time = new Date().toLocaleTimeString();
    const li   = document.createElement("li");
    li.textContent = `[${time}] ${msg}`;
    ul.prepend(li);
}

function delay(ms) { return new Promise(res => setTimeout(res, ms)); }

function clearUI() {
    document.querySelectorAll('.chip, .product').forEach(el => el.remove());
    document.querySelectorAll('.queue-count').forEach(c => c.textContent = '0');
    document.getElementById("logList").innerHTML = "";
    document.getElementById("statsContainer").innerHTML = "<p>Поки статистики немає.</p>";
}

function updateTimer() {
    const msLeft = shiftEndTime - Date.now();
    if (msLeft <= 0) {
        document.getElementById("timeDisplay").textContent = "00:00";
        stopAndFetch();
    } else {
        const secs = Math.floor(msLeft / 1000);
        const m    = String(Math.floor(secs / 60)).padStart(2, '0');
        const s    = String(secs % 60).padStart(2, '0');
        document.getElementById("timeDisplay").textContent = `${m}:${s}`;
    }
}
function startShiftTimer() {
    shiftTimer = setInterval(updateTimer, 1000);
}
async function stopAndFetch() {
    spawnStop = true;
    clearInterval(shiftTimer);
    document.getElementById("stopBtn").disabled = true;
    document.getElementById("startBtn").disabled = false;
    await fetch('/Simulation/Stop', { method: 'POST' });
    const check = setInterval(async () => {
        const running = await (await fetch('/Simulation/IsRunning')).json();
        if (!running) {
            clearInterval(check);
            fetchStats();
        }
    }, 500);
}
async function fetchStats() {
    const data = await (await fetch('/Simulation/GetStats')).json();
    renderStats(data);
}
function renderStats(data) {
    const div = document.getElementById("statsContainer");
    if (!data.stats?.length) {
        div.innerHTML = "<p>Дані статистики відсутні.</p>";
        return;
    }
    let html = `
        <p><strong>Загалом надійшло деталей:</strong> ${data.totalArrived}</p>
        <p><strong>Загалом завершено виробів:</strong> ${data.totalProcessed}</p>
        <p><strong>Залишилося необроблених:</strong> ${data.totalUnprocessed}</p>
    `;
    data.stats.forEach(line => {
        html += `
        <h3>Лінія ${line.lineNumber}</h3>
        <p>Завершено повністю: <strong>${line.completedCount}</strong></p>
        <p>У черзі: <strong>${line.inQueueCount}</strong>, В роботі: <strong>${line.inServiceCount}</strong></p>
        <table class="table table-bordered"><thead><tr>
            <th>Машина</th><th>Використання (%)</th><th>Сер. час у черзі (с)</th>
            <th>Макс. довж. черги</th><th>Сер. час обслуги (с)</th><th>Оброблено машиною</th>
        </tr></thead><tbody>
    `;
        line.machineStats.forEach(m => {
            html += `
            <tr>
                <td>${m.machineIndex + 1}</td>
                <td>${(m.utilization * 100).toFixed(1)}</td>
                <td>${m.averageQueueTime.toFixed(2)}</td>
                <td>${m.maxQueueLength}</td>
                <td>${m.averageServiceTime.toFixed(2)}</td>
                <td>${m.processedCount}</td>
            </tr>
        `;
        });
        html += `</tbody></table>`;
    });
    div.innerHTML = html;
}

// —————— SIGNALR DRIVEN ANIMATION ——————
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/simulationHub")
    .withAutomaticReconnect()
    .build();

connection.on("OnArrival", ev => {
    animators[ev.lineIdx].animateArrival(ev.chipId);
});

connection.on("OnQueueToService", ev => {
    animators[ev.lineIdx].animateQueueToService(ev.chipId);
});

connection.on("OnMachineTransfer", ev => {
    animators[ev.lineIdx].animateTransfer(ev.chipId, ev.fromMachine, ev.toMachine);
});

connection.on("OnCompletion", ev => {
    animators[ev.lineIdx].animateTransfer(ev.chipId, ev.fromMachine, ev.toMachine + 1);
});

connection.start()
    .then(() => logEvent("SignalR: connected"))
    .catch(err => console.error(err));

// —————— BUTTONS & INIT ——————
document.getElementById("applyBtn").addEventListener("click", async () => {
    const form     = document.getElementById("simForm");
    const lines    = +form.elements["LinesCount"].value;
    const machines = +form.elements["MachinesPerLine"].value;
    const html     = await (await fetch(
        `/Simulation?LinesCount=${lines}&MachinesPerLine=${machines}`
    )).text();
    const tmp      = document.createElement("div");
    tmp.innerHTML  = html;
    document.getElementById("animation").replaceWith(
        tmp.querySelector("#animation")
    );
    clearUI();
    setupAnimators();
});

document.getElementById("startBtn").addEventListener("click", async () => {
    clearUI();
    spawnStop = false;
    const form    = document.getElementById("simForm");
    const payload = {
        LinesCount: +form.elements["LinesCount"].value,
        MachinesPerLine: +form.elements["MachinesPerLine"].value,
        ShiftDurationSeconds: +form.elements["ShiftDurationSeconds"].value
    };
    await fetch('/Simulation/StartSimulation', {
        method: 'POST',
        headers: {'Content-Type':'application/json'},
        body: JSON.stringify(payload)
    });
    document.getElementById("startBtn").disabled = true;
    document.getElementById("stopBtn").disabled  = false;
    shiftEndTime = Date.now() + payload.ShiftDurationSeconds * 1000;
    updateTimer();
    startShiftTimer();
});

document.getElementById("stopBtn").addEventListener("click", stopAndFetch);

// Ініціалізація
setupAnimators();