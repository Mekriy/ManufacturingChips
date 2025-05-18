// wwwroot/js/simulation.js

let pollInterval = null;
let timerInterval = null;
let shiftEndTime = null;
let spawnStop = false;

// рівномірний розподіл [mean−dev, mean+dev]
function nextUniform(mean, dev) {
    return mean - dev + Math.random() * (2 * dev);
}

function logEvent(msg) {
    const ul = document.getElementById("logList");
    const time = new Date().toLocaleTimeString();
    const li = document.createElement("li");
    li.textContent = `[${time}] ${msg}`;
    ul.prepend(li);
}

// Рендер статистики
function renderStats(data) {
    const div = document.getElementById("statsContainer");
    if (!data.stats || !data.stats.length) {
        div.innerHTML = "<p>Дані статистики відсутні.</p>";
        return;
    }

    let html = "";
    html += `<p><strong>Загалом надійшло деталей:</strong> ${data.totalArrived}</p>`;
    html += `<p><strong>Загалом завершено виробів:</strong> ${data.totalProcessed}</p>`;
    html += `<p><strong>Залишилося необроблених:</strong> ${data.totalUnprocessed}</p>`;

    data.stats.forEach(line => {
        html += `<h3>Лінія ${line.lineNumber}</h3>`;
        html += `<p>Завершено повністю: <strong>${line.completedCount}</strong></p>`;
        html += `<p>У черзі: <strong>${line.inQueueCount}</strong>, В роботі: <strong>${line.inServiceCount}</strong></p>`;
        html += `<table class="table table-bordered">
<thead><tr>
  <th>Машина</th>
  <th>Використання (%)</th>
  <th>Сер. час у черзі (с)</th>
  <th>Макс. довж. черги</th>
  <th>Сер. час обслуги (с)</th>
  <th>Оброблено машиною</th>
</tr></thead><tbody>`;
        line.machineStats.forEach(m => {
            html += `<tr>
  <td>${m.machineIndex + 1}</td>
  <td>${(m.utilization * 100).toFixed(1)}</td>
  <td>${m.averageQueueTime.toFixed(2)}</td>
  <td>${m.maxQueueLength}</td>
  <td>${m.averageServiceTime.toFixed(2)}</td>
  <td>${m.processedCount}</td>
</tr>`;
        });
        html += `</tbody></table>`;
    });
    div.innerHTML = html;
}

// Анімація одного продукту через всі машини на лінії
async function animateProduct(lineElem, idx) {
    // якщо зупинено або зміна закінчилась — не анімуємо
    if (spawnStop) return;

    const queueBox = lineElem.querySelector(".queue");
    const queueCount = queueBox.querySelector(".queue-count");
    const machines = [...lineElem.querySelectorAll(".machine")];
    const rect = lineElem.getBoundingClientRect();
    const centers = machines.map(m => {
        const r = m.getBoundingClientRect();
        return r.left + r.width/2 - rect.left;
    });
    const finishX = lineElem.clientWidth - 20;

    // enqueue (відображаємо в UI)
    const chip = document.createElement("div");
    chip.className = "chip";
    queueBox.appendChild(chip);
    queueCount.textContent = queueBox.querySelectorAll('.chip').length;
    logEvent(`Лінія ${idx+1}: деталь у чергу`);

    const product = document.createElement("div");
    product.className = "product";
    const startX = queueBox.offsetLeft + queueBox.offsetWidth + 10;
    product.style.left = startX + "px";
    lineElem.appendChild(product);

    for (let m = 0; m < machines.length; m++) {
        const machineCount = machines[m].querySelector('.machine-count');

        // рух
        product.style.transition = `left 0.5s ease-in-out`;
        product.style.left = centers[m] + "px";
        await new Promise(r => setTimeout(r, 500));

        // dequeued
        if (m === 0) {
            queueBox.removeChild(chip);
            queueCount.textContent = queueBox.querySelectorAll('.chip').length;
            logEvent(`Лінія ${idx+1}: вийшла з черги`);
        }

        // сервіс
        const srvParams = [
            { mean: 12/2, dev: 1/2 },
            { mean: 13/2, dev: 3/2 },
            { mean: 7/2,  dev: 1/2 },
            { mean: 8/2,  dev: 3/2 }
        ][m];
        const srv = nextUniform(srvParams.mean, srvParams.dev) * 1000;
        machineCount.textContent = +machineCount.textContent + 1;
        logEvent(`Лінія ${idx+1}, M${m+1}: обслуговування ${(srv/1000).toFixed(2)}с`);
        await new Promise(r => setTimeout(r, srv));
        machineCount.textContent = +machineCount.textContent - 1;

        // передача
        if (m < machines.length - 1) {
            const trParams = [
                { mean: 2/2, dev: 1/2 },
                { mean: 1/2, dev: 1/2 },
                { mean: 3/2, dev: 1/2 }
            ][m];
            const tr = nextUniform(trParams.mean, trParams.dev) * 1000;
            logEvent(`Лінія ${idx+1}, M${m+1}->M${m+2}: передача ${(tr/1000).toFixed(2)}с`);
            await new Promise(r => setTimeout(r, tr));
        }
    }

    // фініш
    product.style.transition = `left 0.5s ease-in-out`;
    product.style.left = finishX + "px";
    await new Promise(r => setTimeout(r, 500));
    product.remove();
    logEvent(`Лінія ${idx+1}: завершено`);
}

// Новий startAnimation — polling arrival подій від бекенда
function startAnimation() {
    spawnStop = false;

    async function pollArrivals() {
        if (spawnStop) return;
        // Отримати всі нові arrivals
        const resp = await fetch('/Simulation/GetArrivals');
        const arrivals = await resp.json();  // масив індексів ліній
        const lines = document.querySelectorAll('.line');
        arrivals.forEach(idx => {
            if (idx >= 0 && idx < lines.length) {
                animateProduct(lines[idx], idx);
            }
        });
        // повторити запит через 200 мс
        setTimeout(pollArrivals, 200);
    }

    pollArrivals();
}

// Зупинка та отримання статистики
async function stopAndFetch() {
    spawnStop = true;
    clearInterval(timerInterval);
    clearInterval(pollInterval);
    document.getElementById("stopBtn").disabled = true;
    document.getElementById("startBtn").disabled = false;

    await fetch('/Simulation/Stop', { method: 'POST' });
    // Чекаємо, поки бекенд завершить
    const check = setInterval(async () => {
        const running = await fetch('/Simulation/IsRunning').then(r => r.json());
        if (!running) {
            clearInterval(check);
            fetchStats();
        }
    }, 500);
}

function updateTimer() {
    const msLeft = shiftEndTime - Date.now();
    if (msLeft <= 0) {
        document.getElementById("timeDisplay").textContent = "00:00";
        stopAndFetch();
    } else {
        const secs = Math.floor(msLeft / 1000);
        const m = String(Math.floor(secs / 60)).padStart(2, '0');
        const s = String(secs % 60).padStart(2, '0');
        document.getElementById("timeDisplay").textContent = `${m}:${s}`;
    }
}

function startPolling() {
    pollInterval = setInterval(async () => {
        const running = await fetch('/Simulation/IsRunning').then(r => r.json());
        if (!running) stopAndFetch();
    }, 1000);
}

async function fetchStats() {
    const data = await fetch('/Simulation/GetStats').then(r => r.json());
    renderStats(data);
}

// Обробники кнопок
document.getElementById("applyBtn").addEventListener("click", async () => {
    const form = document.getElementById("simForm");
    const lines = +form.elements["LinesCount"].value;
    const machines = +form.elements["MachinesPerLine"].value;
    const html = await fetch(`/Simulation?LinesCount=${lines}&MachinesPerLine=${machines}`)
        .then(r => r.text());
    const temp = document.createElement("div");
    temp.innerHTML = html;
    const newAnim = temp.querySelector("#animation");
    document.getElementById("animation").replaceWith(newAnim);
});

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

    document.getElementById("startBtn").disabled = true;
    document.getElementById("stopBtn").disabled = false;
    shiftEndTime = Date.now() + payload.ShiftDurationSeconds * 1000;
    updateTimer();
    timerInterval = setInterval(updateTimer, 1000);

    startAnimation();
    startPolling();
});

document.getElementById("stopBtn").addEventListener("click", stopAndFetch);
