'use strict';

// Интерфейс намеренно без сборки и без зависимостей: открыл dotnet run — и он работает.
// Состояние опрашивается раз в секунду; всё состояние бота живёт в памяти процесса, поэтому
// снимок отдаётся одним запросом и ничего склеивать на клиенте не нужно.

const POLL_MS = 1000;

const el = (id) => document.getElementById(id);

const STATUS_LABELS = {
    Stopped: 'Остановлен',
    Starting: 'Запускается',
    Running: 'Работает',
    Stopping: 'Останавливается',
    Faulted: 'Авария',
};

const STATUS_CLASSES = {
    Stopped: 'chip-stopped',
    Starting: 'chip-pending',
    Running: 'chip-running',
    Stopping: 'chip-pending',
    Faulted: 'chip-faulted',
};

const LEVEL_LABELS = {
    Idle: 'свободен',
    Placing: 'ставится',
    Working: 'в рынке',
    PartiallyFilled: 'залит частично',
    TakeProfit: 'тейк выставлен',
};

const LEVEL_TAGS = {
    Working: 'tag-working',
    Placing: 'tag-working',
    TakeProfit: 'tag-takeprofit',
    PartiallyFilled: 'tag-partial',
};

const PURPOSE_LABELS = {
    Entry: 'рыночный вход',
    LevelEntry: 'добор уровня',
    LevelTakeProfit: 'тейк уровня',
    Flatten: 'закрытие позиции',
};

const DIRECTION_LABELS = { Long: 'Long (вниз)', Short: 'Short (вверх)' };
const SIDE_LABELS = { Buy: 'покупка', Sell: 'продажа' };

let priceDecimals = 2;
let volumeDecimals = 3;

// ───────────────────────────── форматирование ─────────────────────────────

function num(value, decimals) {
    if (value === null || value === undefined) return '—';
    return Number(value).toLocaleString('ru-RU', {
        minimumFractionDigits: decimals,
        maximumFractionDigits: decimals,
    });
}

const price = (v) => num(v, priceDecimals);
const volume = (v) => num(v, volumeDecimals);
const money = (v) => num(v, 2);

function signed(node, value, decimals = 2) {
    if (value === null || value === undefined) {
        node.textContent = '—';
        node.className = 'num muted';
        return;
    }

    const sign = value > 0 ? '+' : '';
    node.textContent = sign + num(value, decimals);
    node.className = 'num ' + (value > 0 ? 'positive' : value < 0 ? 'negative' : 'muted');
}

/** Знаков после запятой — по шагу сетки и объёму уровней, чтобы не терять точность и не сорить нулями. */
function inferDecimals(state) {
    const fromStep = decimalsOf(state.step) || decimalsOf(state.lastPrice);
    if (fromStep) priceDecimals = Math.min(8, Math.max(2, fromStep));

    const level = state.levels && state.levels.length ? state.levels[0].volume : null;
    const fromVolume = decimalsOf(level);
    if (fromVolume !== null) volumeDecimals = Math.min(8, Math.max(0, fromVolume));
}

function decimalsOf(value) {
    if (!value) return null;
    const text = String(value);
    const dot = text.indexOf('.');
    return dot < 0 ? 0 : text.length - dot - 1;
}

function timeOf(iso) {
    if (!iso) return '—';
    return new Date(iso).toLocaleTimeString('ru-RU');
}

// ───────────────────────────── отрисовка ─────────────────────────────

function render(state) {
    inferDecimals(state);

    el('symbol').textContent = state.symbol;

    const env = el('environment');
    env.textContent = state.venue + ' · ' + state.environment;
    env.className = 'chip ' + (state.environment === 'Live' ? 'chip-live' : 'chip-demo');

    const status = el('status');
    status.textContent = STATUS_LABELS[state.status] || state.status;
    status.className = 'chip ' + (STATUS_CLASSES[state.status] || 'chip-stopped');

    el('price').textContent = state.lastPrice ? price(state.lastPrice) : '—';
    el('tickAge').textContent = state.lastTickAt ? 'тик в ' + timeOf(state.lastTickAt) : 'нет данных';

    const banner = el('banner');
    if (state.faultMessage) {
        banner.textContent = state.faultMessage;
        banner.classList.remove('hidden');
    } else {
        banner.classList.add('hidden');
    }

    renderPosition(state);
    renderRisk(state);
    renderCycle(state);
    renderStats(state.stats);
    renderLevels(state);
    renderOrders(state.orders);
    renderEvents(state.events);
    renderButtons(state.status);
}

function renderPosition(state) {
    const p = state.position;

    el('posSide').textContent = p.side ? SIDE_LABELS[p.side] : 'нет позиции';
    el('posVolume').textContent = p.volume ? volume(p.volume) : '—';
    el('posAverage').textContent = p.averagePrice ? price(p.averagePrice) : '—';

    signed(el('posUnrealized'), p.unrealizedPnl);
    signed(el('posRealized'), p.realizedPnl);

    el('posFees').textContent = money(p.fees);
    el('posFees').className = 'num muted';

    signed(el('posNet'), p.netPnl + p.unrealizedPnl);
}

function renderRisk(state) {
    const r = state.risk;

    el('riskTake').textContent = r.takeProfitPrice ? price(r.takeProfitPrice) : 'не задан';
    el('riskStop').textContent = r.stopLossPrice ? price(r.stopLossPrice) : 'не задан';
    el('riskTrailing').textContent = r.trailingStopPrice ? price(r.trailingStopPrice) : 'не взведён';
    el('riskExtreme').textContent = r.trailingExtreme ? price(r.trailingExtreme) : '—';
    el('riskCommitted').textContent = volume(r.committedVolume);
    el('riskLimit').textContent = r.maxPositionVolume ? volume(r.maxPositionVolume) : 'без лимита';
}

function renderCycle(state) {
    el('cycleNum').textContent = state.cycle || '—';
    el('cycleDirection').textContent = DIRECTION_LABELS[state.direction] || state.direction;
    el('cycleAnchor').textContent = state.anchorPrice ? price(state.anchorPrice) : '—';
    el('cycleStep').textContent = state.step ? price(state.step) : '—';
    el('cycleLevels').textContent = state.levels.length || '—';
    el('cycleStarted').textContent = state.startedAt ? timeOf(state.startedAt) : '—';
}

function renderStats(s) {
    el('statLevelCycles').textContent = s.levelCycles;
    el('statFills').textContent = s.fills;
    el('statTake').textContent = s.takeProfitCycles;
    el('statStop').textContent = s.stopLossCycles;

    const dd = el('statDrawdown');
    dd.textContent = s.maxDrawdown ? '-' + money(s.maxDrawdown) : '0,00';
    dd.className = 'num ' + (s.maxDrawdown ? 'negative' : 'muted');
}

function renderLevels(state) {
    const body = el('levels').querySelector('tbody');

    if (!state.levels.length) {
        body.innerHTML = '<tr><td class="empty" colspan="9">Сетка не построена — цикл начнётся на первом тике.</td></tr>';
        return;
    }

    body.innerHTML = state.levels
        .map((l) => {
            const rowClass = l.status === 'TakeProfit' || l.status === 'PartiallyFilled'
                ? 'filled'
                : l.status === 'Working' || l.status === 'Placing'
                    ? 'working'
                    : '';

            const tag = LEVEL_TAGS[l.status] || '';

            return `<tr class="${rowClass}">
                <td>${l.index}</td>
                <td>${price(l.price)}</td>
                <td>${volume(l.volume)}</td>
                <td><span class="tag ${tag}">${LEVEL_LABELS[l.status] || l.status}</span></td>
                <td>${l.filledVolume ? volume(l.filledVolume) : '—'}</td>
                <td>${l.filledPrice ? price(l.filledPrice) : '—'}</td>
                <td>${l.takeProfitPrice ? price(l.takeProfitPrice) : '—'}</td>
                <td>${l.cycles || '—'}</td>
                <td class="${l.realizedPnl > 0 ? 'positive' : l.realizedPnl < 0 ? 'negative' : 'muted'}">
                    ${l.realizedPnl ? money(l.realizedPnl) : '—'}
                </td>
            </tr>`;
        })
        .join('');
}

function renderOrders(orders) {
    const body = el('orders').querySelector('tbody');

    if (!orders.length) {
        body.innerHTML = '<tr><td class="empty" colspan="7">Заявок в рынке нет.</td></tr>';
        return;
    }

    body.innerHTML = orders
        .map(
            (o) => `<tr>
                <td>${PURPOSE_LABELS[o.purpose] || o.purpose}</td>
                <td>${o.levelIndex ?? '—'}</td>
                <td><span class="tag ${o.side === 'Buy' ? 'tag-buy' : 'tag-sell'}">${SIDE_LABELS[o.side]}</span></td>
                <td>${o.kind === 'Market' ? 'рыночная' : 'лимитная'}</td>
                <td>${o.kind === 'Market' ? '—' : price(o.price)}</td>
                <td>${volume(o.quantity)}</td>
                <td>${o.filled ? volume(o.filled) : '—'}</td>
            </tr>`,
        )
        .join('');
}

function renderEvents(events) {
    const list = el('events');

    if (!events.length) {
        list.innerHTML = '<li class="empty">Пока ничего не происходило.</li>';
        return;
    }

    list.innerHTML = events
        .map(
            (e) => `<li class="level-${e.level}">
                <time>${timeOf(e.timestamp)}</time>
                <span class="kind">${escapeHtml(e.kind)}</span>
                <span>${escapeHtml(e.message)}</span>
            </li>`,
        )
        .join('');
}

function escapeHtml(text) {
    return String(text).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
}

function renderButtons(status) {
    const running = status === 'Running';
    const busy = status === 'Starting' || status === 'Stopping';

    el('btnStart').disabled = running || busy;
    el('btnStop').disabled = !running && status !== 'Starting';
    el('btnClose').disabled = !running;
}

// ───────────────────────────── команды ─────────────────────────────

let environment = 'Demo Trading';

async function command(path, confirmText) {
    if (confirmText && environment === 'Live' && !window.confirm(confirmText)) return;

    const buttons = document.querySelectorAll('.btn');
    buttons.forEach((b) => (b.disabled = true));

    try {
        const response = await fetch('/api/' + path, { method: 'POST' });
        const body = await response.json().catch(() => ({}));
        if (!response.ok) window.alert(body.error || 'Команда не выполнена.');
    } catch (error) {
        window.alert('Сервер недоступен: ' + error.message);
    } finally {
        await poll();
    }
}

el('btnStart').addEventListener('click', () =>
    command('start', 'Счёт боевой — бот начнёт торговать реальными деньгами. Запустить?'),
);

el('btnStop').addEventListener('click', () =>
    command('stop', 'Остановка закроет позицию по рынку. Продолжить?'),
);

el('btnClose').addEventListener('click', () =>
    command('close', 'Позиция будет закрыта по рынку, сетка перестроится от текущей цены. Продолжить?'),
);

// ───────────────────────────── опрос ─────────────────────────────

async function poll() {
    try {
        const response = await fetch('/api/state');
        if (!response.ok) return;

        const state = await response.json();
        environment = state.environment;
        render(state);
    } catch {
        const status = el('status');
        status.textContent = 'нет связи';
        status.className = 'chip chip-faulted';
    }
}

async function loadSettings() {
    try {
        const response = await fetch('/api/settings');
        el('settings').textContent = JSON.stringify(await response.json(), null, 2);
    } catch (error) {
        el('settings').textContent = 'Настройки получить не удалось: ' + error.message;
    }
}

loadSettings();
poll();
setInterval(poll, POLL_MS);
