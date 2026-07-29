"use strict";

(function installPeopleFinanceRoutes() {
  const routes = {
    crm: {
      title: "Customers & CRM",
      subtitle: "Customer profiles, follow-ups, quotations and loyalty"
    },
    finance: {
      title: "Receivables & cash",
      subtitle: "Debtor collection, supplier payments and cashbook"
    },
    hrm: {
      title: "People & HRM",
      subtitle: "Employees, attendance, leave and payroll"
    }
  };

  function activateRoute(pageId) {
    const route = routes[pageId];
    if (!route) return;

    const oldUrl = location.href;
    history.replaceState(null, "", `#${pageId}`);

    const title = document.querySelector("#pageTitle");
    const subtitle = document.querySelector("#pageSubtitle");
    if (title) title.textContent = route.title;
    if (subtitle) subtitle.textContent = route.subtitle;
    document.title = `${route.title} · Nexus POS`;

    document.querySelectorAll(".nav-button").forEach((button) => {
      const active = button.dataset.page === pageId;
      button.classList.toggle("active", active);
      if (active) button.setAttribute("aria-current", "page");
      else button.removeAttribute("aria-current");
    });

    const application = document.querySelector("#application");
    application?.classList.remove("sidebar-open");

    const palette = document.querySelector("#commandPalette");
    if (palette?.open) palette.close();
    document.body.classList.remove("nexus-no-scroll");

    const page = document.querySelector("#page");
    if (page) {
      delete page.dataset.pfhWorkspace;
      page.innerHTML = '<div class="page-loading" aria-live="polite"><div class="skeleton"></div><div class="skeleton" style="min-height:340px"></div></div>';
    }

    window.dispatchEvent(new HashChangeEvent("hashchange", {
      oldURL: oldUrl,
      newURL: location.href
    }));
  }

  document.addEventListener("click", (event) => {
    const target = event.target.closest("[data-command-page], [data-page]");
    const pageId = target?.dataset.commandPage || target?.dataset.page;
    if (!routes[pageId]) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    activateRoute(pageId);
  }, true);
})();
