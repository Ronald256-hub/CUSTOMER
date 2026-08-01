import { chromium } from "playwright-core";

const baseUri = process.env.NEXUS_TEST_BASE_URI;
const username = process.env.NEXUS_TEST_USERNAME;
const password = process.env.NEXUS_TEST_PASSWORD;

if (!baseUri || !username || !password) {
  throw new Error("Browser validation requires base URI and login credentials.");
}

const browser = await chromium.launch({ channel: "msedge", headless: true });
const context = await browser.newContext({
  viewport: { width: 1440, height: 980 },
  colorScheme: "light",
  reducedMotion: "reduce"
});

const page = await context.newPage();
const pageErrors = [];
const consoleErrors = [];
const httpErrors = [];

page.on("pageerror", (error) => pageErrors.push(error.message));
page.on("console", (message) => {
  if (message.type() === "error") consoleErrors.push(message.text());
});
page.on("response", (response) => {
  if (response.status() >= 400) httpErrors.push({ status: response.status(), url: response.url() });
});

async function openModule(search, pageId) {
  await page.getByRole("button", { name: "Open module command palette" }).click();
  await page.getByLabel("Search modules").fill(search);
  await page.locator(`#commandPalette [data-command-page="${pageId}"]`).click();
}

try {
  await page.goto(baseUri, { waitUntil: "networkidle" });
  await page.getByLabel("Username", { exact: true }).fill(username);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in securely" }).click();

  await page.locator(".nexus-command-centre").waitFor({ state: "visible" });
  await page.getByRole("heading", { name: "Everything requiring attention, on one screen." }).waitFor();
  await page.getByText("Short-glass liquid monitor", { exact: true }).waitFor();
  await page.getByText("Nexus Gate Short Glass", { exact: true }).waitFor();
  await page.getByText("3", { exact: true }).first().waitFor();

  const desktopOverflow = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 2
  );
  if (desktopOverflow) throw new Error("The desktop application shell has horizontal overflow.");

  await openModule("inventory", "inventory");
  await page.getByRole("heading", { name: "Inventory", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Inventory movement centre", exact: true }).waitFor();
  await page.getByText("Branch stock cards", { exact: true }).waitFor();
  await page.getByLabel("Stock view").selectOption("low");
  await page.locator("#inventoryWorkspaceCount").waitFor();

  await openModule("procurement", "procurement");
  await page.getByRole("heading", { name: "Procurement", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Procurement workspace", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Purchase orders" }).click();
  await page.getByRole("tab", { name: "Goods receipts" }).click();
  await page.getByText("Review need", { exact: true }).waitFor();

  await openModule("customers", "crm");
  await page.getByRole("heading", { name: "CRM transactional workspace", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Customer profiles", exact: true }).waitFor();
  await page.getByRole("button", { name: "Create customer profile" }).waitFor();
  await page.getByRole("tab", { name: "Follow-ups" }).click();
  await page.getByRole("heading", { name: "Schedule follow-up", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Quotations" }).click();
  await page.getByRole("heading", { name: "Quotation pipeline", exact: true }).waitFor();

  await openModule("receivables", "finance");
  await page.getByRole("heading", { name: "Receivables, payables and cashbook", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Supplier obligations" }).click();
  await page.getByRole("heading", { name: "Open supplier obligations", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Cashbook" }).click();
  await page.getByRole("heading", { name: "Posted cash movement", exact: true }).waitFor();

  await openModule("people", "hrm");
  await page.getByRole("heading", { name: "People, attendance, leave and payroll", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Employee profiles", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Attendance" }).click();
  await page.getByRole("heading", { name: "Today’s attendance", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Leave" }).click();
  await page.getByRole("heading", { name: "Leave requests", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Payroll" }).click();
  await page.getByRole("heading", { name: "Payroll periods", exact: true }).waitFor();

  await page.getByRole("button", { name: "Executive intelligence" }).click();
  await page.getByRole("heading", { name: "Executive intelligence control tower", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Business performance pulse", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Payment mix", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Risk radar", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Short-glass quantity and revenue watch", exact: true }).waitFor();
  await page.getByLabel("Reporting scope").selectOption("shop");
  await page.getByRole("button", { name: "Refresh intelligence" }).click();
  await page.getByText("Active branch", { exact: true }).last().waitFor();
  await page.getByRole("button", { name: "Last 7 days" }).click();
  await page.getByRole("heading", { name: "Executive intelligence control tower", exact: true }).waitFor();
  await page.getByRole("button", { name: "Export intelligence CSV" }).waitFor();
  await page.getByRole("button", { name: "Print control tower" }).waitFor();

  await openModule("approvals", "exceptions");
  await page.getByRole("heading", { name: "Approvals and exception centre", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Priority queue", exact: true }).waitFor();
  await page.getByRole("tab", { name: "Approvals", exact: true }).click();
  await page.getByLabel("Severity").selectOption("urgent");
  await page.locator("#exceptionVisibleCount").waitFor();
  await page.getByRole("button", { name: "Refresh centre", exact: true }).click();
  await page.getByRole("heading", { name: "Approvals and exception centre", exact: true }).waitFor();
  await page.getByRole("button", { name: "Export CSV", exact: true }).waitFor();
  await page.getByRole("button", { name: "Print", exact: true }).waitFor();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload({ waitUntil: "networkidle" });
  await page.getByRole("heading", { name: "Approvals and exception centre", exact: true }).waitFor();
  await page.getByRole("button", { name: "Open navigation" }).waitFor();
  await page.getByRole("button", { name: "Open navigation" }).click();

  const sidebarOpen = await page.locator("#application").evaluate((element) =>
    element.classList.contains("sidebar-open")
  );
  if (!sidebarOpen) throw new Error("Mobile navigation did not open.");

  await page.getByRole("button", { name: "Close navigation" }).click();
  const mobileOverflow = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 2
  );
  if (mobileOverflow) throw new Error("The mobile application shell has horizontal overflow.");

  const unlabeledInteractive = await page.evaluate(() => {
    const elements = [...document.querySelectorAll("button, input, select, textarea, a[href]")];
    return elements.filter((element) => {
      if (element.hidden || element.closest(".hidden")) return false;
      const text = (element.textContent || "").trim();
      const aria = element.getAttribute("aria-label") || "";
      const title = element.getAttribute("title") || "";
      const id = element.id;
      const label = id ? document.querySelector(`label[for="${CSS.escape(id)}"]`) : null;
      const wrapped = element.closest("label");
      return !text && !aria && !title && !label && !wrapped;
    }).map((element) => element.outerHTML.slice(0, 180));
  });

  if (unlabeledInteractive.length) {
    throw new Error(`Unlabelled interactive controls: ${unlabeledInteractive.join(" | ")}`);
  }
  if (pageErrors.length) throw new Error(`Browser page errors: ${pageErrors.join(" | ")}`);

  const unexpectedHttpErrors = httpErrors.filter(({ status, url }) => {
    const path = new URL(url).pathname;
    const expectedAnonymousProbe = status === 401 && path === "/api/v3/auth/me";
    const expectedMissingFavicon = status === 404 && path === "/favicon.ico";
    return !expectedAnonymousProbe && !expectedMissingFavicon;
  });
  if (unexpectedHttpErrors.length) {
    throw new Error(`Unexpected HTTP failures: ${unexpectedHttpErrors.map(({ status, url }) => `${status} ${url}`).join(" | ")}`);
  }

  const relevantConsoleErrors = consoleErrors.filter((message) =>
    !message.includes("Failed to load resource") && !message.includes("favicon")
  );
  if (relevantConsoleErrors.length) {
    throw new Error(`Browser console errors: ${relevantConsoleErrors.join(" | ")}`);
  }

  console.log("Nexus operator, stock, CRM, finance, HRM, executive intelligence and approvals browser validation passed.");
} finally {
  await browser.close();
}
