"use strict";

(function stabilisePeopleFinanceNavigation() {
  const managed = new Set(["crm", "finance", "hrm"]);

  function notifyWhenSettled(pageId, attempt = 0) {
    if (location.hash === `#${pageId}`) {
      window.dispatchEvent(new Event("hashchange"));
      return;
    }

    if (attempt < 60) {
      setTimeout(() => notifyWhenSettled(pageId, attempt + 1), 50);
    }
  }

  document.addEventListener("click", (event) => {
    const target = event.target.closest("[data-command-page], [data-page]");
    const pageId = target?.dataset.commandPage || target?.dataset.page;
    if (managed.has(pageId)) {
      setTimeout(() => notifyWhenSettled(pageId), 0);
    }
  });
})();
