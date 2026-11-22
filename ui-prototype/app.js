(function prototypeRuntime() {
  "use strict";

  const DAY_MS = 86_400_000;
  const PERIOD_DAYS = 30;
  const ENTRY_KEY = "vape-meter.prototype.entries.v1";
  const THEME_KEY = "vape-meter.prototype.theme.v1";
  const DEMO_OFFSETS = [112, 101, 93, 83, 72, 64, 54, 45, 36, 28, 21, 10, 3];
  const FORECAST_CYCLES = 8;
  const DAY_NAMES = ["воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота"];
  const dateFormat = options => new Intl.DateTimeFormat("ru-RU", { timeZone: "UTC", ...options });
  const shortDate = dateFormat({ day: "numeric", month: "short" });
  const mediumDate = dateFormat({ day: "numeric", month: "long" });
  const fullDate = dateFormat({ day: "numeric", month: "long", year: "numeric" });
  const dayAndMonth = dateFormat({ day: "2-digit", month: "2-digit" });
  const decimal = new Intl.NumberFormat("ru-RU", { maximumFractionDigits: 1 });

  const $ = id => document.getElementById(id);
  const dateAt = day => new Date(day * DAY_MS);
  const isoDate = day => dateAt(day).toISOString().slice(0, 10);
  const format = (formatter, day) => formatter.format(dateAt(day));
  const icon = name => `<svg class="icon" aria-hidden="true"><use href="#i-${name}"/></svg>`;
  const mean = list => list.reduce((sum, value) => sum + value, 0) / list.length;
  const sortDays = days => [...days].sort((a, b) => a - b);
  const bound = (value, lower, upper) => Math.min(upper, Math.max(lower, value));

  function localDay() {
    const now = new Date();
    return Math.floor(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate()) / DAY_MS);
  }

  function parseDay(value) {
    if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return NaN;
    const day = Date.parse(`${value}T00:00:00Z`) / DAY_MS;
    return Number.isInteger(day) && isoDate(day) === value ? day : NaN;
  }

  function wordForm(value, forms) {
    const remainder = Math.abs(value) % 100;
    const digit = remainder % 10;
    if (remainder >= 11 && remainder <= 14) return forms[2];
    return digit === 1 ? forms[0] : digit >= 2 && digit <= 4 ? forms[1] : forms[2];
  }

  const daysText = value => `${value} ${wordForm(value, ["день", "дня", "дней"])}`;
  const today = localDay();
  const seedEntries = () => DEMO_OFFSETS.map(offset => today - offset);

  function readLocal(key, fallback) {
    try {
      const value = localStorage.getItem(key);
      return value === null ? fallback : JSON.parse(value);
    } catch {
      return fallback;
    }
  }

  function writeLocal(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
      return true;
    } catch {
      return false;
    }
  }

  const initialEntries = readLocal(ENTRY_KEY, null);
  const storedDays = Array.isArray(initialEntries) ? initialEntries.map(parseDay) : null;
  const state = {
    entries: storedDays?.every(day => Number.isInteger(day) && day <= today)
      ? sortDays(new Set(storedDays)) : seedEntries(),
    theme: readLocal(THEME_KEY, "dark") === "light" ? "light" : "dark",
    page: "overview",
    period: 0,
    selected: today,
    chartScope: "period"
  };

  const pages = {
    overview: ["Обзор", "Календарь замен и срок службы картриджей"],
    history: ["История", "Все зарегистрированные замены"],
    analytics: ["Аналитика", "Продолжительность циклов и динамика замен"],
    settings: ["Настройки", "Оформление и сброс демонстрационной истории"]
  };

  const calendar = $("calendar-grid");
  const editor = $("editor");
  const editorDate = $("entry-date");
  const confirmation = $("confirm-dialog");
  const menu = $("date-menu");
  const chartStage = $("chart-stage");
  const chartScroll = chartStage.parentElement;
  const chartCanvas = $("chart-canvas");
  const chartPoints = $("chart-points");
  const chartTooltip = $("chart-tip");
  const chartContext = chartCanvas.getContext("2d");
  const minimumDay = parseDay("0001-01-01");
  let editorDay = null;
  let pendingConfirmation;
  let menuDate;
  let toastTimer;
  let pinnedPoint = null;
  let visiblePoint = null;
  let chartData = [];
  let chartLayout = [];

  function range(offset = state.period) {
    const end = today - offset * PERIOD_DAYS;
    return { start: Math.max(minimumDay, end - PERIOD_DAYS + 1), end };
  }

  function intervalSeries(entries) {
    return entries.slice(1).map((end, index) => ({
      start: entries[index],
      end,
      days: end - entries[index]
    }));
  }

  function within(day, period) {
    return day >= period.start && day <= period.end;
  }

  function dateRange(start, end) {
    if (start === end) return format(shortDate, end);
    return `${format(shortDate, start)} — ${format(shortDate, end)}`;
  }

  function periodText(period) {
    if (dateAt(period.start).getUTCFullYear() !== dateAt(period.end).getUTCFullYear()) {
      return `${format(fullDate, period.start)} — ${format(fullDate, period.end)}`;
    }
    return `${format(shortDate, period.start)} — ${format(fullDate, period.end)}`;
  }

  function renderCalendar() {
    const period = range();
    $("calendar-title").textContent = state.period === 0 ? "Последние 30 дней" : "Предыдущий период";
    $("period-label").textContent = periodText(period);
    $("period-next").disabled = state.period === 0;
    $("period-prev").disabled = period.start === minimumDay;
    const leading = (dateAt(period.start).getUTCDay() + 6) % 7;
    const entryDays = new Set(state.entries);
    const cells = Array.from({ length: leading }, () => '<span aria-hidden="true"></span>');
    for (let day = period.start; day <= period.end; day++) {
      const selected = day === state.selected;
      const recorded = entryDays.has(day);
      const date = dateAt(day);
      const weekend = [0, 6].includes(date.getUTCDay());
      const classes = ["day", selected ? "is-selected" : "", day === today ? "is-today" : "", recorded ? "has-entry" : "", weekend ? "is-weekend" : ""];
      const marker = recorded ? `<span class="day-marker" aria-hidden="true">${icon("pod")}</span>` : "";
      const details = [format(fullDate, day)];
      if (day === today) details.push("сегодня");
      if (recorded) details.push("есть замена");
      cells.push(`<button type="button" class="${classes.join(" ")}" data-day="${day}" tabindex="${selected ? 0 : -1}" aria-pressed="${selected}" ${day === today ? 'aria-current="date"' : ""} aria-label="${details.join(", ")}">${date.getUTCDate()}${marker}</button>`);
    }
    calendar.innerHTML = cells.join("");
    renderSelectedDate();
  }

  function renderSelectedDate() {
    const day = state.selected;
    const hasEntry = state.entries.includes(day);
    const label = day === today ? "Сегодня" : DAY_NAMES[dateAt(day).getUTCDay()].replace(/^./u, letter => letter.toUpperCase());
    $("selected-date").textContent = `${label}, ${format(mediumDate, day)}`;
    $("selected-status").textContent = hasEntry ? "Замена записана" : "Нет записи о замене";
    $("day-icon").innerHTML = icon(hasEntry ? "check" : "calendar");
    $("day-icon").classList.toggle("has-entry", hasEntry);
    $("day-actions").innerHTML = hasEntry
      ? `<button class="button secondary" data-action="edit" type="button">${icon("edit")}Изменить</button>
         <button class="icon-button" data-action="delete" type="button" title="Удалить замену" aria-label="Удалить замену">${icon("trash")}</button>`
      : `<button class="button secondary" data-action="add" type="button">${icon("plus")}Добавить</button>`;
  }

  function focusDay(day) {
    calendar.querySelector(`[data-day="${day}"]`)?.focus({ preventScroll: true });
  }

  function selectDay(day, moveFocus = false) {
    state.selected = day;
    renderCalendar();
    if (moveFocus) focusDay(day);
  }

  function changePeriod(step, moveFocus = false) {
    const requested = state.period + step;
    if (requested < 0 || range(requested).end < minimumDay) return;
    state.period = requested;
    state.selected = range().end;
    render();
    if (moveFocus) focusDay(state.selected);
  }

  function jumpTo(day) {
    state.period = Math.floor((today - day) / PERIOD_DAYS);
    state.selected = day;
    render();
  }

  function renderMetrics(cycles) {
    const inPeriod = cycles.filter(point => within(point.end, range()));
    const durations = inPeriod.map(point => point.days);
    const previous = cycles.filter(point => within(point.end, range(state.period + 1))).map(point => point.days);
    const currentMean = durations.length ? mean(durations) : null;
    const difference = currentMean !== null && previous.length
      ? Math.round((currentMean - mean(previous)) * 10) / 10 : null;
    const trend = difference !== null
      ? `<span class="metric-change" title="Изменение среднего срока к предыдущим 30 дням" aria-label="Изменение к предыдущему периоду ${difference > 0 ? "+" : ""}${decimal.format(difference)} дня">${difference > 0 ? "+" : ""}${decimal.format(difference)} д.</span>`
      : "";
    const metrics = [
      { name: "Средний срок", value: currentMean, icon: "clock", unit: "дн.", trend },
      { name: "Замен за период", value: state.entries.filter(day => within(day, range())).length, icon: "layers" },
      { name: "Минимум", value: durations.length ? Math.min(...durations) : null, icon: "down", unit: "дн." },
      { name: "Максимум", value: durations.length ? Math.max(...durations) : null, icon: "up", unit: "дн." }
    ];
    $("metrics").innerHTML = metrics.map(metric => `
      <article class="card metric">
        <span class="tile-icon ${metric.icon === "clock" ? "mint" : "violet"}">${icon(metric.icon)}</span>
        <div><p class="metric-label">${metric.name}</p>
        <p class="metric-value">${metric.value === null ? "—" : decimal.format(metric.value)}${metric.unit && metric.value !== null ? `<small>${metric.unit}</small>` : ""}${metric.trend ?? ""}</p></div>
      </article>`).join("");
    $("analytics-period-label").textContent = periodText(range());
    $("analytics-next").disabled = state.period === 0;
    $("analytics-prev").disabled = range().start === minimumDay;
  }

  function renderForecast(cycles) {
    const last = state.entries.at(-1);
    const elapsed = last === undefined ? null : today - last;
    const recent = cycles.slice(-FORECAST_CYCLES).map(point => point.days);
    const estimated = recent.length >= 2 ? Math.round(mean(recent)) : null;
    const hasRange = recent.length >= 3;
    const forecastDay = estimated === null ? null : last + estimated;

    $("forecast-orbit").style.setProperty("--progress", estimated === null ? "0%" : `${Math.min(100, 100 * elapsed / estimated)}%`);
    $("forecast-value").textContent = forecastDay === null ? "—" : format(shortDate, forecastDay);
    let note = "мало данных";
    if (forecastDay !== null) {
      const remaining = forecastDay - today;
      note = remaining > 0 ? `через ${daysText(remaining)}` : remaining === 0 ? "сегодня" : `${daysText(-remaining)} назад`;
    }
    $("forecast-note").textContent = note;
    $("forecast-range").textContent = hasRange ? dateRange(last + Math.min(...recent), last + Math.max(...recent)) : "—";
    $("forecast-current").textContent = elapsed === null ? "Нет записи" : `${daysText(elapsed)} в работе`;
    if (hasRange) {
      $("forecast-caption").textContent = `По ${recent.length} последним завершённым циклам. Диапазон — по фактическому разбросу.`;
    } else if (recent.length === 2) {
      $("forecast-caption").textContent = "По 2 завершённым циклам. Диапазон появится после третьего.";
    } else {
      $("forecast-caption").textContent = "Для прогноза нужны хотя бы 2 завершённых цикла (3 замены).";
    }
  }

  function renderHistory(cycles) {
    const durations = new Map(cycles.map(point => [point.end, point.days]));
    const count = state.entries.length;
    $("history-count").textContent = `${count} ${wordForm(count, ["запись", "записи", "записей"])}`;
    $("history-empty").hidden = count > 0;
    $("history-list").innerHTML = [...state.entries].reverse().map(day => `
      <li class="history-item">
        <span class="day-status-icon has-entry">${icon("check")}</span>
        <button class="history-date" data-action="show" data-day="${day}" type="button" title="Показать в календаре">
          ${format(fullDate, day)}<span class="subtle">${durations.has(day) ? `${daysText(durations.get(day))} с предыдущей замены` : "Первая запись"}</span>
        </button>
        <button class="icon-button" data-action="edit" data-day="${day}" type="button" title="Изменить" aria-label="Изменить замену за ${format(fullDate, day)}">${icon("edit")}</button>
        <button class="icon-button" data-action="delete" data-day="${day}" type="button" title="Удалить" aria-label="Удалить замену за ${format(fullDate, day)}">${icon("trash")}</button>
      </li>`).join("");
  }

  function renderChart(cycles) {
    chartData = state.chartScope === "all" ? cycles : cycles.filter(point => within(point.end, range()));
    pinnedPoint = null;
    showChartPoint(null);
    chartStage.hidden = chartData.length === 0;
    $("chart-footer").hidden = chartData.length === 0;
    $("chart-empty").hidden = chartData.length > 0;
    $("chart-empty").textContent = state.chartScope === "all"
      ? "Добавьте две замены, чтобы увидеть первый завершённый цикл."
      : "В этом периоде нет завершённых циклов. Можно открыть всю историю.";
    $("chart-subtitle").textContent = state.chartScope === "all"
      ? "Дни между заменами · вся история" : `Дни между заменами · ${periodText(range())}`;
    for (const button of document.querySelectorAll("[data-scope]")) {
      button.setAttribute("aria-pressed", String(button.dataset.scope === state.chartScope));
    }
    chartStage.style.minWidth = chartData.length > 1 ? `${54 + (chartData.length - 1) * 35}px` : "";
    chartPoints.innerHTML = chartData.map((point, index) => `
      <button type="button" class="chart-point" data-point="${index}" aria-pressed="false" aria-label="${daysText(point.days)}, ${dateRange(point.start, point.end)}"></button>`).join("");
    if (!chartData.length) return;
    $("chart-average").innerHTML = `<i class="legend-line"></i>Среднее: ${decimal.format(mean(chartData.map(point => point.days)))} дн.`;
    drawChart();
    chartScroll.scrollLeft = chartScroll.scrollWidth;
  }

  function drawChart() {
    if (!chartData.length || chartStage.clientWidth < 1) return;
    const width = chartStage.clientWidth;
    const height = chartStage.clientHeight;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    chartCanvas.width = Math.round(width * dpr);
    chartCanvas.height = Math.round(height * dpr);
    chartContext.scale(dpr, dpr);
    const css = getComputedStyle(document.documentElement);
    const color = property => css.getPropertyValue(property).trim();
    const ctx = chartContext;
    const left = 27;
    const right = width - 24;
    const top = 12;
    const bottom = height - 24;
    const durations = chartData.map(point => point.days);
    const highest = Math.max(...durations);
    const rawStep = Math.max(1, highest / 4);
    const magnitude = 10 ** Math.floor(Math.log10(rawStep));
    const tickStep = [1, 2, 3, 5, 10].find(multiplier => multiplier * magnitude >= rawStep) * magnitude;
    const ceiling = Math.max(tickStep, Math.ceil(highest / tickStep) * tickStep);
    const y = value => bottom - value / ceiling * (bottom - top);
    const x = index => chartData.length === 1 ? (left + right) / 2 : left + (right - left) * index / (chartData.length - 1);
    chartLayout = chartData.map((point, index) => ({ x: x(index), y: y(point.days) }));

    ctx.lineWidth = 1;
    ctx.font = "11px Segoe UI, sans-serif";
    ctx.fillStyle = color("--muted");
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    for (let value = 0; value <= ceiling; value += tickStep) {
      ctx.strokeStyle = color("--border");
      ctx.beginPath();
      ctx.moveTo(left, y(value));
      ctx.lineTo(right, y(value));
      ctx.stroke();
      ctx.fillText(String(value), left - 6, y(value));
    }

    if (chartData.length > 1) {
      const gradient = ctx.createLinearGradient(0, top, 0, bottom);
      gradient.addColorStop(0, color("--chart-wash"));
      gradient.addColorStop(1, "transparent");
      ctx.beginPath();
      chartLayout.forEach((point, index) => index ? ctx.lineTo(point.x, point.y) : ctx.moveTo(point.x, point.y));
      ctx.lineTo(right, bottom);
      ctx.lineTo(left, bottom);
      ctx.closePath();
      ctx.fillStyle = gradient;
      ctx.fill();
    }

    const averageY = y(mean(durations));
    ctx.beginPath();
    ctx.setLineDash([5, 5]);
    ctx.strokeStyle = color("--mint");
    ctx.globalAlpha = .72;
    ctx.moveTo(left, averageY);
    ctx.lineTo(right, averageY);
    ctx.stroke();
    ctx.globalAlpha = 1;
    ctx.setLineDash([]);

    ctx.beginPath();
    const chartStroke = ctx.createLinearGradient(left, 0, right, 0);
    chartStroke.addColorStop(0, color("--violet-strong"));
    chartStroke.addColorStop(.58, color("--violet"));
    chartStroke.addColorStop(1, color("--violet-strong"));
    ctx.strokeStyle = chartStroke;
    ctx.shadowColor = color("--chart-halo") || "transparent";
    ctx.shadowBlur = 10;
    ctx.lineWidth = 2.8;
    ctx.lineJoin = "round";
    chartLayout.forEach((point, index) => index ? ctx.lineTo(point.x, point.y) : ctx.moveTo(point.x, point.y));
    ctx.stroke();
    ctx.shadowBlur = 0;

    ctx.fillStyle = color("--muted");
    ctx.textAlign = "center";
    const spacing = chartData.length > 1 ? (right - left) / (chartData.length - 1) : width;
    const labelStep = Math.max(1, Math.ceil(54 / spacing));
    const lastIndex = chartData.length - 1;
    for (const [index, point] of chartData.entries()) {
      if (index === lastIndex || (index % labelStep === 0 && lastIndex - index >= labelStep)) {
        ctx.fillText(format(dayAndMonth, point.end), x(index), bottom + 13);
      }
      const button = chartPoints.children[index];
      button.style.left = `${chartLayout[index].x}px`;
      button.style.top = `${chartLayout[index].y}px`;
    }
    if (visiblePoint !== null) showChartPoint(visiblePoint);
  }

  function showChartPoint(index) {
    visiblePoint = index;
    for (const button of chartPoints.children) {
      button.classList.toggle("is-active", Number(button.dataset.point) === index);
      button.setAttribute("aria-pressed", String(Number(button.dataset.point) === pinnedPoint));
    }
    const point = chartData[index];
    const position = chartLayout[index];
    if (index === null || !point || !position) {
      chartTooltip.hidden = true;
      return;
    }
    chartTooltip.innerHTML = `<strong>${daysText(point.days)}</strong>${dateRange(point.start, point.end)}`;
    chartTooltip.hidden = false;
    const viewportLeft = chartScroll.scrollLeft;
    const viewportRight = viewportLeft + chartScroll.clientWidth;
    const left = bound(position.x - chartTooltip.offsetWidth / 2, viewportLeft + 3, viewportRight - chartTooltip.offsetWidth - 3);
    const top = position.y > chartTooltip.offsetHeight + 16 ? position.y - chartTooltip.offsetHeight - 12 : position.y + 13;
    chartTooltip.style.left = `${left}px`;
    chartTooltip.style.top = `${top}px`;
  }

  function render() {
    const cycles = intervalSeries(state.entries);
    renderCalendar();
    renderMetrics(cycles);
    renderForecast(cycles);
    renderHistory(cycles);
    renderChart(cycles);
  }

  function changeView(view, focus = true) {
    closeMenu(false);
    state.page = view;
    document.querySelector("main").dataset.page = view;
    const [title, description] = pages[view];
    $("page-title").textContent = title;
    $("page-description").textContent = description;
    for (const link of document.querySelectorAll(".nav-link")) {
      const selected = link.dataset.view === view;
      link.classList.toggle("is-current", selected);
      if (selected) link.setAttribute("aria-current", "page");
      else link.removeAttribute("aria-current");
    }
    drawChart();
    window.scrollTo(0, 0);
    if (focus) $("page-title").focus({ preventScroll: true });
  }

  function changeTheme(theme, save = true) {
    state.theme = theme;
    document.documentElement.dataset.theme = theme;
    const button = document.querySelector(".theme-switch");
    button.innerHTML = icon(theme === "dark" ? "sun" : "moon");
    const label = theme === "dark" ? "Включить светлую тему" : "Включить тёмную тему";
    button.setAttribute("aria-label", label);
    button.title = label;
    for (const option of document.querySelectorAll("[data-theme-value]")) {
      option.setAttribute("aria-pressed", String(option.dataset.themeValue === theme));
    }
    drawChart();
    if (save && !writeLocal(THEME_KEY, theme)) notify("Тема изменена до закрытия вкладки");
  }

  function notify(message) {
    window.clearTimeout(toastTimer);
    $("toast-text").textContent = message;
    $("toast").hidden = false;
    toastTimer = window.setTimeout(() => { $("toast").hidden = true; }, 3200);
  }

  function saveEntries(entries, message) {
    state.entries = sortDays(new Set(entries));
    const saved = writeLocal(ENTRY_KEY, state.entries.map(isoDate));
    notify(saved ? message : `${message}. Изменение доступно до закрытия вкладки.`);
  }

  function openEditor(day, previous = null) {
    editorDay = previous;
    $("editor-title").textContent = previous === null ? "Добавить замену" : "Изменить дату замены";
    $("editor-error").textContent = "";
    editorDate.removeAttribute("aria-invalid");
    editorDate.max = isoDate(today);
    editorDate.min = isoDate(minimumDay);
    editorDate.value = isoDate(day);
    editor.showModal();
    editorDate.focus();
  }

  function returnToDayOrHeading(day) {
    if (state.page === "overview") focusDay(day);
    else $("page-title").focus({ preventScroll: true });
  }

  function confirmChange(title, message, label, action) {
    $("confirm-title").textContent = title;
    $("confirm-text").textContent = message;
    $("confirm-action").textContent = label;
    pendingConfirmation = action;
    confirmation.showModal();
  }

  function runDateAction(action, day) {
    if (action === "add") openEditor(day);
    if (action === "edit") openEditor(day, day);
    if (action === "delete") {
      confirmChange("Удалить замену?", `Запись за ${format(fullDate, day)} будет удалена из истории.`, "Удалить", () => {
        saveEntries(state.entries.filter(entry => entry !== day), "Запись удалена");
        render();
        returnToDayOrHeading(day);
      });
    }
    if (action === "show") {
      jumpTo(day);
      changeView("overview", false);
      focusDay(day);
    }
  }

  function openMenu(day, left, top) {
    selectDay(day);
    menuDate = day;
    const recorded = state.entries.includes(day);
    menu.innerHTML = recorded
      ? `<button role="menuitem" type="button" data-action="edit">${icon("edit")}Изменить дату</button>
         <button role="menuitem" type="button" data-action="delete" class="delete">${icon("trash")}Удалить замену</button>`
      : `<button role="menuitem" type="button" data-action="add">${icon("plus")}Добавить замену картриджа</button>`;
    menu.hidden = false;
    menu.style.left = `${bound(left, 6, innerWidth - menu.offsetWidth - 6)}px`;
    menu.style.top = `${bound(top, 6, innerHeight - menu.offsetHeight - 6)}px`;
    menu.firstElementChild.focus({ preventScroll: true });
  }

  function closeMenu(restoreFocus = true) {
    if (menu.hidden) return;
    menu.hidden = true;
    if (restoreFocus) focusDay(menuDate);
  }

  document.querySelector(".navigation").addEventListener("click", event => {
    const link = event.target.closest("[data-view]");
    if (link && link.dataset.view !== state.page) changeView(link.dataset.view);
  });
  $("period-prev").addEventListener("click", () => changePeriod(1));
  $("period-next").addEventListener("click", () => changePeriod(-1));
  $("analytics-prev").addEventListener("click", () => changePeriod(1));
  $("analytics-next").addEventListener("click", () => changePeriod(-1));
  $("page-add").addEventListener("click", () => {
    openEditor(state.page === "overview" && !state.entries.includes(state.selected) ? state.selected : today);
  });
  $("day-actions").addEventListener("click", event => {
    const action = event.target.closest("[data-action]")?.dataset.action;
    if (action) runDateAction(action, state.selected);
  });
  $("history-list").addEventListener("click", event => {
    const button = event.target.closest("[data-action]");
    if (button) runDateAction(button.dataset.action, Number(button.dataset.day));
  });

  calendar.addEventListener("click", event => {
    const button = event.target.closest("[data-day]");
    if (button) selectDay(Number(button.dataset.day), true);
  });
  calendar.addEventListener("contextmenu", event => {
    const button = event.target.closest("[data-day]");
    if (!button) return;
    event.preventDefault();
    const box = button.getBoundingClientRect();
    openMenu(Number(button.dataset.day), event.clientX || box.left, event.clientY || box.bottom);
  });
  calendar.addEventListener("keydown", event => {
    const button = event.target.closest("[data-day]");
    if (!button) return;
    const day = Number(button.dataset.day);
    const period = range();
    const offsets = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 };
    if (event.key in offsets || event.key === "Home" || event.key === "End") {
      event.preventDefault();
      let next = day + offsets[event.key];
      if (event.key === "Home") next = period.start;
      if (event.key === "End") next = period.end;
      selectDay(bound(next, period.start, period.end), true);
    } else if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
      event.preventDefault();
      const box = button.getBoundingClientRect();
      openMenu(day, box.left, box.bottom);
    } else if (event.key === "PageUp" || event.key === "PageDown") {
      event.preventDefault();
      changePeriod(event.key === "PageUp" ? 1 : -1, true);
    }
  });
  menu.addEventListener("click", event => {
    const action = event.target.closest("[data-action]")?.dataset.action;
    if (!action) return;
    closeMenu();
    runDateAction(action, menuDate);
  });
  menu.addEventListener("keydown", event => {
    const items = Array.from(menu.querySelectorAll("button"));
    if (event.key === "Escape") {
      event.preventDefault();
      closeMenu();
    } else if (event.key === "Tab") {
      closeMenu();
    } else if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
      event.preventDefault();
      const currentIndex = items.indexOf(document.activeElement);
      const lastIndex = items.length - 1;
      const nextIndex = { Home: 0, End: lastIndex, ArrowDown: (currentIndex + 1) % items.length, ArrowUp: (currentIndex + lastIndex) % items.length }[event.key];
      items[nextIndex].focus();
    }
  });
  document.addEventListener("pointerdown", event => {
    if (!menu.hidden && !menu.contains(event.target)) closeMenu();
  });
  window.addEventListener("resize", () => closeMenu(false));
  window.addEventListener("scroll", () => closeMenu(false), { passive: true });

  $("editor-form").addEventListener("submit", event => {
    event.preventDefault();
    const date = parseDay(editorDate.value);
    const reject = message => {
      $("editor-error").textContent = message;
      editorDate.setAttribute("aria-invalid", "true");
      editorDate.focus();
    };
    if (!Number.isInteger(date) || date < minimumDay || date > today) {
      reject("Выберите дату не позже сегодняшней.");
      return;
    }
    if (state.entries.includes(date) && date !== editorDay) {
      reject("На эту дату уже есть замена.");
      return;
    }
    saveEntries([...state.entries.filter(day => day !== editorDay), date], editorDay === null ? "Замена добавлена" : "Дата замены изменена");
    editor.close();
    jumpTo(date);
    returnToDayOrHeading(date);
  });
  editorDate.addEventListener("input", () => {
    $("editor-error").textContent = "";
    editorDate.removeAttribute("aria-invalid");
  });
  for (const button of document.querySelectorAll("[data-close]")) {
    button.addEventListener("click", () => $(button.dataset.close).close());
  }
  $("confirm-action").addEventListener("click", () => {
    const action = pendingConfirmation;
    confirmation.close();
    pendingConfirmation = null;
    action?.();
  });
  $("reset-data").addEventListener("click", () => {
    confirmChange("Восстановить демоданные?", "Текущая история будет заменена исходными демонстрационными записями.", "Сбросить", () => {
      saveEntries(seedEntries(), "Демоданные восстановлены");
      state.chartScope = "period";
      jumpTo(today);
    });
  });
  document.querySelector(".theme-switch").addEventListener("click", () => changeTheme(state.theme === "dark" ? "light" : "dark"));
  for (const option of document.querySelectorAll("[data-theme-value]")) {
    option.addEventListener("click", () => changeTheme(option.dataset.themeValue));
  }

  for (const button of document.querySelectorAll("[data-scope]")) {
    button.addEventListener("click", () => {
      if (state.chartScope === button.dataset.scope) return;
      state.chartScope = button.dataset.scope;
      renderChart(intervalSeries(state.entries));
    });
  }
  chartPoints.addEventListener("pointerover", event => {
    const button = event.target.closest("[data-point]");
    if (button && event.pointerType !== "touch") showChartPoint(Number(button.dataset.point));
  });
  chartPoints.addEventListener("pointerout", event => {
    if (event.target.closest("[data-point]") && event.pointerType !== "touch") showChartPoint(pinnedPoint);
  });
  chartPoints.addEventListener("focusin", event => showChartPoint(Number(event.target.dataset.point)));
  chartPoints.addEventListener("focusout", () => showChartPoint(pinnedPoint));
  chartPoints.addEventListener("click", event => {
    const button = event.target.closest("[data-point]");
    if (!button) return;
    const index = Number(button.dataset.point);
    pinnedPoint = pinnedPoint === index ? null : index;
    showChartPoint(pinnedPoint);
  });
  chartPoints.addEventListener("keydown", event => {
    const index = Number(event.target.dataset.point);
    if (event.key === "Escape") {
      pinnedPoint = null;
      showChartPoint(null);
    }
    if (event.key === "ArrowRight" || event.key === "ArrowLeft") {
      event.preventDefault();
      const next = bound(index + (event.key === "ArrowRight" ? 1 : -1), 0, chartData.length - 1);
      chartPoints.children[next].focus();
    }
  });
  chartScroll.addEventListener("scroll", () => {
    if (visiblePoint !== null) showChartPoint(visiblePoint);
  }, { passive: true });
  new ResizeObserver(drawChart).observe(chartStage);

  changeTheme(state.theme, false);
  render();
})();
