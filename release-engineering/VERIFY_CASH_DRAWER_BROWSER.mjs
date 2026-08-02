import { chromium } from "playwright-core";

const baseUri = process.env.NEXUS_TEST_BASE_URI;
const username = process.env.NEXUS_TEST_USERNAME;
const password = process.env.NEXUS_TEST_PASSWORD;

if (!baseUri || !username || !password) {
  throw new Error("Cash drawer browser validation requires base URI and login credentials.");
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

try {
  await page.goto(baseUri, { waitUntil: "networkidle" });
  await page.getByLabel("Username", { exact: true }).fill(username);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in securely" }).click();
  await page.locator("#application:not(.hidden)").waitFor();

  await page.getByRole("button", { name: "Open module command palette" }).click();
  await page.getByLabel("Search modules").fill("cash drawer");
  await page.locator('[data-command-page="cash-drawer"]').click();

  await page.getByRole("heading", { name: "Cash drawer and shift reconciliation", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Record drawer movement", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Count cash by denomination", exact: true }).waitFor();
  await page.getByRole("heading", { name: "Shift reconciliation queue", exact: true }).waitFor();
  await page.getByRole("button", { name: "Record drawer movement", exact: true }).waitFor();
  await page.getByRole("button", { name: "Record denomination count", exact: true }).waitFor();
  await page.getByText("11,000 UGX", { exact: true }).first().waitFor();

  const desktopOverflow = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 2
  );
  if (desktopOverflow) throw new Error("The cash drawer desktop workspace has horizontal overflow.");

  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload({ waitUntil: "networkidle" });
  await page.getByRole("heading", { name: "Cash drawer and shift reconciliation", exact: true }).waitFor();
  await page.getByRole("button", { name: "Open navigation" }).waitFor();

  const mobileOverflow = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 2
  );
  if (mobileOverflow) throw new Error("The cash drawer mobile workspace has horizontal overflow.");

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
    throw new Error(`Unlabelled cash drawer controls: ${unlabeledInteractive.join(" | ")}`);
  }

  if (pageErrors.length) throw new Error(`Browser page errors: ${pageErrors.join(" | ")}`);
  const unexpectedHttpErrors = httpErrors.filter(({ status, url }) => {
    const path = new URL(url).pathname;
    return !(status === 401 && path === "/api/v3/auth/me") &&
      !(status === 404 && path === "/favicon.ico");
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

  console.log("Nexus cash drawer desktop and mobile Microsoft Edge validation passed.");
} finally {
  await browser.close();
}
