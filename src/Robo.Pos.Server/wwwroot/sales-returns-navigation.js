"use strict";

(function installSalesReturnsRoute() {
  const routeId = "sales-returns";
  const routeTitle = "Sales returns";
  const routeSubtitle = "Controlled refunds, stock disposition and credit notes";

  function isAdministrator() {
    return String(document.querySelector("#userRole")?.textContent || "")
      .trim().toLowerCase() === "admin";
  }

  function activeRoute() {
    return location.hash.replace(/^#/, "") === routeId;
  }

  function ensureNavigationButton() {
    if (!isAdministrator()) return;
    const navigation = document.querySelector("#navigation");
    if (!navigation || navigation.querySelector(`[data-page="${routeId}"]`)) return;

    const groups = [...navigation.querySelectorAll(".nav-group")];
    const operations = groups.find((group) => {
      const title = group.querySelector(".nav-group-title")?.textContent.trim().toLowerCase() || "";
      return title === "operations" || title === "sales";
    }) || groups.find((group) => group.querySelector('[data-page="sales"]')) || groups[0];
    if (!operations) return;

    const button = document.createElement("button");
    button.className = "nav-button";
    button.type = "button";
    button.dataset.page = routeId;
    button.title = `${routeTitle} — ${routeSubtitle}`;
    button.innerHTML = `<span class="nav-icon" aria-hidden="true">RT</span><span class="nav-label">${routeTitle}</span>`;

    const receipts = operations.querySelector('[data-page="receipts"]');
    const sales = operations.querySelector('[data-page="sales"]');
    (receipts || sales)?.insertAdjacentElement("afterend", button) || operations.appendChild(button);
    updateActiveState();
  }

  function ensureCommandResult() {
    if (!isAdministrator()) return;
    const host = document.querySelector("#commandResults");
    const dialog = document.querySelector("#commandPalette");
    if (!host || !dialog?.open || host.querySelector(`[data-command-page="${routeId}"]`)) return;

    const query = String(document.querySelector("#commandSearch")?.value || "").trim().toLowerCase();
    const searchable = `${routeTitle} returns refund refunds credit note credit notes after sales`;
    if (query && !searchable.includes(query)) return;

    const button = document.createElement("button");
    button.type = "button";
    button.className = "command-result";
    button.dataset.commandPage = routeId;
    button.innerHTML = `<span class="nav-icon" aria-hidden="true">RT</span><span><strong>${routeTitle}</strong><small>${routeSubtitle}</small></span>`;
    host.prepend(button);
  }

  function updateActiveState() {
    document.querySelectorAll(".nav-button").forEach((button) => {
      const selected = button.dataset.page === routeId && activeRoute();
      if (button.dataset.page === routeId || selected) {
        button.classList.toggle("active", selected);
        if (selected) button.setAttribute("aria-current", "page");
        else button.removeAttribute("aria-current");
      } else if (activeRoute()) {
        button.classList.remove("active");
        button.removeAttribute("aria-current");
      }
    });
  }

  function closeTransientNavigation() {
    const dialog = document.querySelector("#commandPalette");
    if (dialog?.open) dialog.close();
    document.querySelector("#application")?.classList.remove("sidebar-open");
  }

  function activateRoute(replaceHistory = true) {
    if (!isAdministrator()) return;
    ensureNavigationButton();
    closeTransientNavigation();
    if (replaceHistory) history.replaceState(null, "", `#${routeId}`);
    const title = document.querySelector("#pageTitle");
    const subtitle = document.querySelector("#pageSubtitle");
    if (title) title.textContent = routeTitle;
    if (subtitle) subtitle.textContent = routeSubtitle;
    document.title = `${routeTitle} · Nexus POS`;
    updateActiveState();
    window.NexusSalesReturns?.render();
  }

  document.addEventListener("click", (event) => {
    const target = event.target.closest(`[data-page="${routeId}"], [data-command-page="${routeId}"]`);
    if (!target) return;
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();
    activateRoute(true);
  }, true);

  window.addEventListener("hashchange", (event) => {
    if (!activeRoute()) return;
    event.stopImmediatePropagation();
    activateRoute(false);
  }, true);

  document.querySelector("#commandSearch")?.addEventListener("input", () => {
    queueMicrotask(ensureCommandResult);
  });

  new MutationObserver(() => {
    ensureNavigationButton();
    ensureCommandResult();
    if (activeRoute()) {
      updateActiveState();
      const page = document.querySelector("#page");
      if (page && !page.querySelector(".sales-returns-workspace") && !window.NexusSalesReturns?.isRendering()) {
        window.NexusSalesReturns?.render();
      }
    }
  }).observe(document.documentElement, { childList: true, subtree: true });

  ensureNavigationButton();
  ensureCommandResult();
  if (activeRoute()) setTimeout(() => activateRoute(false), 0);
})();
