// wwwroot/js/simulation.js

let intervals = [];
let pollInterval = null;
let timerInterval = null;
let shiftEndTime = null;

function logEvent(msg) {
    const ul = document.getElementById("logList");
    const time = new Date().toLocaleTimeString();
    const li = document.createElement("li");
    li.textContent = `[${time}] ${msg}`;
    ul.prepend(li);
}

// Animate each line with randomized 1–2 s delays
function startAnimation() {
    document.querySelectorAll(".line").forEach((lineElem, idx) => {
        const queueBox = lineElem.querySelector(".queue");
        const machines = [...lineElem.querySelectorAll(".machine")];
        const rect = lineElem.getBoundingClientRect();
        const centers = machines.map(m => {
            const r = m.getBoundingClientRect();
            return r.left + r.width/2 - rect.left;
        });
        const finishX = lineElem.clientWidth - 20;

        function spawn() {
            // enqueue visual chip
            const chip = document.createElement("div");
            chip.className = "chip";
            queueBox.appendChild(chip);
            logEvent(`Лінія ${idx+1}: мікросхема в чергу`);

            // animate product moving through machines
            const product = document.createElement("div");
            product.className = "product";
            const startX = queueBox.offsetLeft + queueBox.offsetWidth + 10;
            product.style.left = startX + "px";
            lineElem.appendChild(product);

            const path = [startX, startX + 10, ...centers, finishX];
            let step = 0;
            function move() {
                if (step === 1 && queueBox.firstElementChild) {
                    queueBox.removeChild(queueBox.firstElementChild);
                    logEvent(`Лінія ${idx+1}: вийшла з черги`);
                }
                if (step < path.length) {
                    product.style.left = path[step] + "px";
                    step++;
                    setTimeout(move, 600);
                } else {
                    product.remove();
                    logEvent(`Лінія ${idx+1}: завершено`);
                }
            }
            move();

            // schedule next spawn in 1–2 sec
            const delay = Math.random() * 1000 + 1000;
            intervals[idx] = setTimeout(spawn, delay);
        }

        // kick it off
        spawn();
    });
}

document.getElementById("applyBtn").addEventListener("click", async () => {
    const form = document.getElementById("simForm");
    const lines = +form.elements["LinesCount"].value;
    const machines = +form.elements["MachinesPerLine"].value;
    const url = `/Simulation?LinesCount=${lines}&MachinesPerLine=${machines}`;
    const html = await fetch(url).then(r => r.text());
    const temp = document.createElement("div");
    temp.innerHTML = html;
    const newAnim = temp.querySelector("#animation");
    document.getElementById("animation").replaceWith(newAnim);
});

async function handleStop() {
    // clear all timeouts
    intervals.forEach(clearTimeout);
    intervals = [];
    clearInterval(timerInterval);
    document.getElementById("timeDisplay").textContent = "00:00";
    document.getElementById("stopBtn").disabled = true;
    await fetch('/Simulation/Stop', { method: 'POST' });
}

function updateTimer() {
    const msLeft = shiftEndTime - Date.now();
    if (msLeft <= 0) {
        document.getElementById("timeDisplay").textContent = "00:00";
        clearInterval(timerInterval);
    } else {
        const secs = Math.floor(msLeft/1000);
        const m = String(Math.floor(secs/60)).padStart(2,'0');
        const s = String(secs%60).padStart(2,'0');
        document.getElementById("timeDisplay").textContent = `${m}:${s}`;
    }
}

function startPolling() {
    pollInterval = setInterval(async () => {
        const running = await fetch('/Simulation/IsRunning').then(r => r.json());
        if (!running) {
            clearInterval(pollInterval);
            intervals.forEach(clearTimeout);
            clearInterval(timerInterval);
            fetchStats();
            document.getElementById("startBtn").disabled = false;
            document.getElementById("stopBtn").disabled = true;
        }
    }, 1000);
}

function renderStats(data) {
    const div = document.getElementById("statsContainer");
    if (!data.stats || !data.stats.length) {
        div.innerHTML = "<p>Дані статистики відсутні.</p>";
        return;
    }
    let html = `<p><strong>Згенеровано продуктів:</strong> ${data.total}</p>`;
    data.stats.forEach(line => {
        html += `<h3>Лінія ${line.lineNumber}</h3>`;
        html += `<table class="table table-striped">
<thead><tr>
  <th>Машина</th><th>Завантаження (%)</th><th>Сер. час в черзі (с)</th>
  <th>Сер. час обслуг. (с)</th><th>Макс. черга</th><th>Оброблено</th>
</tr></thead><tbody>`;
        line.machineStats.forEach(m => {
            html += `<tr>
  <td>${m.machineIndex}</td>
  <td>${(m.utilization*100).toFixed(2)}</td>
  <td>${m.averageQueueTime.toFixed(2)}</td>
  <td>${m.averageServiceTime.toFixed(2)}</td>
  <td>${m.maxQueueLength}</td>
  <td>${m.processedProducts}</td>
</tr>`;
        });
        html += `</tbody></table>`;
    });
    div.innerHTML = html;
}

async function fetchStats() {
    const data = await fetch('/Simulation/GetStats').then(r => r.json());
    renderStats(data);
}

document.getElementById("startBtn").addEventListener("click", async () => {
    const form = document.getElementById("simForm");
    const payload = {
        LinesCount: +form.elements["LinesCount"].value,
        MachinesPerLine: +form.elements["MachinesPerLine"].value,
        ShiftDurationSeconds: +form.elements["ShiftDurationSeconds"].value
    };

    await fetch('/Simulation/StartSimulation', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    // UI state
    document.getElementById("startBtn").disabled = true;
    document.getElementById("stopBtn").disabled = false;

    // countdown
    shiftEndTime = Date.now() + payload.ShiftDurationSeconds * 1000;
    updateTimer();
    timerInterval = setInterval(updateTimer, 1000);

    startAnimation();
    startPolling();
});

document.getElementById("stopBtn").addEventListener("click", handleStop);
