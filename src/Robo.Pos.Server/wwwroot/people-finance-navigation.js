"use strict";

(function stabilisePeopleFinanceNavigation() {
  const managed = new Set(["crm", "finance", "hrm"]);
  let navigationSerial = 0;

  function notifyWhenSettled(pageId, serial, attempt = 0) {
    if (serial !== navigationSerial) {
      return;
    }

    if (location.hash === `#${pageId}`) {
      window.dispatchEvent(new Event("hashchange"));
      return;
    }

    if (attempt < 600) {
      setTimeout(() => notifyWhenSettled(pageId, serial, attempt + 1), 50);
    }
  }

  document.addEventListener("click", (event) => {
    const target = event.target.closest("[data-command-page], [data-page]");
    const pageId = target?.dataset.commandPage || target?.dataset.page;
    if (managed.has(pageId)) {
      const serial = ++navigationSerial;
      setTimeout(() => notifyWhenSettled(pageId, serial), 0);
    }
  });
})();
