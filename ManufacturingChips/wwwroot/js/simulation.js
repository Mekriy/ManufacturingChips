// wwwroot/js/simulation.js

let pollArrivalsTimer    = null;
let pollCompletionsTimer = null;
let shiftTimer           = null;
let shiftEndTime         = null;
let spawnStop            = false;

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
      <table class="table table-bordered">
        <thead><tr>
          <th>Машина</th>
          <th>Використання (%)</th>
          <th>Сер. час у черзі (с)</th>
          <th>Макс. довж. черги</th>
          <th>Сер. час обслуги (с)</th>
          <th>Оброблено машиною</th>
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

// Анімація появи в черзі (enqueue)
function animateArrival(lineIdx) {
    if (spawnStop) return;
    const lineElem = document.querySelectorAll('.line')[lineIdx];
    const queueBox = lineElem.querySelector('.queue');
    const countDom = queueBox.querySelector('.queue-count');
    const chip = document.createElement('div');
    chip.className = 'chip';
    queueBox.appendChild(chip);
    countDom.textContent = queueBox.querySelectorAll('.chip').length;
    logEvent(`Лінія ${lineIdx+1}: enqueue`);
}

// Анімація повного проходження по машинах (completion)
async function animateCompletion(lineIdx) {
    if (spawnStop) return;
    const lineElem = document.querySelectorAll('.line')[lineIdx];
    const machines = [...lineElem.querySelectorAll('.machine')];
    const queueBox = lineElem.querySelector('.queue');
    const countDom = queueBox.querySelector('.queue-count');

    // Видаляємо перший чіп з черги
    const firstChip = queueBox.querySelector('.chip');
    if (firstChip) {
        queueBox.removeChild(firstChip);
        countDom.textContent = queueBox.querySelectorAll('.chip').length;
        logEvent(`Лінія ${lineIdx+1}: dequeued`);
    }

    // Створюємо продукт і анімуємо рух крізь машини
    const product = document.createElement('div');
    product.className = 'product';
    lineElem.appendChild(product);
    const rect = lineElem.getBoundingClientRect();
    const centers = machines.map(m => {
        const r = m.getBoundingClientRect();
        return r.left + r.width/2 - rect.left;
    });

    for (let m = 0; m < machines.length; m++) {
        // рух до машини
        product.style.transition = 'left 0.5s ease-in-out';
        product.style.left = centers[m] + 'px';
        await new Promise(r => setTimeout(r, 500));

        // сервіс (ті ж параметри, що й у бекенді, але лише для затримки UI)
        const srvParams = [
            { mean: 3, dev: 0.25 },
            { mean: 3.75, dev: 0.75 },
            { mean: 1.75, dev: 0.5 },
            { mean: 2, dev: 0.75 }
        ][m];
        const srv = nextUniform(srvParams.mean, srvParams.dev) * 1000;
        logEvent(`Лінія ${lineIdx+1}, M${m+1}: service ${(srv/1000).toFixed(2)}с`);
        await new Promise(r => setTimeout(r, srv));

        // передача
        if (m < machines.length - 1) {
            const trParams = [
                { mean: 0.5, dev: 0.25 },
                { mean: 0.25, dev: 0.25 },
                { mean: 0.75, dev: 0.25 }
            ][m];
            const tr = nextUniform(trParams.mean, trParams.dev) * 1000;
            logEvent(`Лінія ${lineIdx+1}, M${m+1}->M${m+2}: transfer ${(tr/1000).toFixed(2)}с`);
            await new Promise(r => setTimeout(r, tr));
        }
    }

    // фініш
    product.style.transition = 'left 0.5s ease-in-out';
    product.style.left = (lineElem.clientWidth - 20) + 'px';
    await new Promise(r => setTimeout(r, 500));
    product.remove();
    logEvent(`Лінія ${lineIdx+1}: completion`);
}

// Запуск анімації: polling arrivals & completions
function startAnimation() {
    spawnStop = false;

    async function pollArrivals() {
        if (spawnStop) return;
        const resp = await fetch('/Simulation/GetArrivals');
        const arrivals = await resp.json();
        arrivals.forEach(idx => animateArrival(idx));
        pollArrivalsTimer = setTimeout(pollArrivals, 200);
    }

    async function pollCompletions() {
        if (spawnStop) return;
        const resp = await fetch('/Simulation/GetCompletions');
        const comps = await resp.json();
        comps.forEach(idx => animateCompletion(idx));
        pollCompletionsTimer = setTimeout(pollCompletions, 200);
    }

    pollArrivals();
    pollCompletions();
}

// Зупинка та отримання статистики
async function stopAndFetch() {
    spawnStop = true;
    clearTimeout(pollArrivalsTimer);
    clearTimeout(pollCompletionsTimer);
    clearInterval(timerInterval);

    document.getElementById("stopBtn").disabled   = true;
    document.getElementById("startBtn").disabled  = false;

    await fetch('/Simulation/Stop', { method: 'POST' });

    // чекаємо, поки бекенд відпрацює всі потоки
    const check = setInterval(async () => {
        const running = await fetch('/Simulation/IsRunning').then(r=>r.json());
        if (!running) {
            clearInterval(check);
            fetchStats();
        }
    }, 500);
}

// Таймер зміни
function updateTimer() {
    const msLeft = shiftEndTime - Date.now();
    if (msLeft <= 0) {
        document.getElementById("timeDisplay").textContent = "00:00";
        stopAndFetch();             // автоматично зупинаємо
    } else {
        const secs = Math.floor(msLeft/1000);
        const m = String(Math.floor(secs/60)).padStart(2,'0');
        const s = String(secs%60).padStart(2,'0');
        document.getElementById("timeDisplay").textContent = `${m}:${s}`;
    }
}

function startPolling() {
    timerInterval = setInterval(updateTimer, 1000);
}

// Fetch та відмалювати статистику
async function fetchStats() {
    const data = await fetch('/Simulation/GetStats').then(r => r.json());
    renderStats(data);
}

// Apply параметри без перезавантаження сторінки
document.getElementById("applyBtn").addEventListener("click", async () => {
    const form = document.getElementById("simForm");
    const lines = +form.elements["LinesCount"].value;
    const machines = +form.elements["MachinesPerLine"].value;
    const html = await fetch(`/Simulation?LinesCount=${lines}&MachinesPerLine=${machines}`)
        .then(r=>r.text());
    const tmp = document.createElement("div");
    tmp.innerHTML = html;
    const newAnim = tmp.querySelector("#animation");
    document.getElementById("animation").replaceWith(newAnim);
});

// Start
document.getElementById("startBtn").addEventListener("click", async () => {
    const form = document.getElementById("simForm");
    const payload = {
        LinesCount: +form.elements["LinesCount"].value,
        MachinesPerLine: +form.elements["MachinesPerLine"].value,
        ShiftDurationSeconds: +form.elements["ShiftDurationSeconds"].value
    };

    await fetch('/Simulation/StartSimulation', {
        method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify(payload)
    });

    document.getElementById("startBtn").disabled  = true;
    document.getElementById("stopBtn").disabled   = false;

    shiftEndTime = Date.now() + payload.ShiftDurationSeconds*1000;
    updateTimer();
    startPolling();
    startAnimation();
});

// Manual Stop
document.getElementById("stopBtn").addEventListener("click", stopAndFetch);
