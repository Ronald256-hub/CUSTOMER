import { chromium } from "playwright-core";

const baseUri = process.env.NEXUS_TEST_BASE_URI;
const username = process.env.NEXUS_TEST_USERNAME;
const password = process.env.NEXUS_TEST_PASSWORD;

if (!baseUri || !username || !password) {
  throw new Error("Browser validation requires base URI and login credentials.");
}

const browser = await chromium.launch({
  channel: "msedge",
  headless: true
});

const context = await browser.newContext({
  viewport: { width: 1440, height: 980 },
  colorScheme: "light",
  reducedMotion: "reduce"
});

const page = await context.newPage();
const pageErrors = [];
const consoleErrors = [];

page.on("pageerror", (error) => pageErrors.push(error.message));
page.on("console", (message) => {
  if (message.type() === "error") {
    consoleErrors.push(message.text());
  }
});

try {
  await page.goto(baseUri, { waitUntil: "networkidle" });
  await page.getByLabel("Username").fill(username);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in securely" }).click();

  await page.locator(".nexus-command-centre").waitFor({ state: "visible" });
  await page.getByRole("heading", { name: "Everything requiring attention, on one screen." }).waitFor();
  await page.getByText("Short-glass liquid monitor", { exact: true }).waitFor();
  await page.getByText("Nexus Gate Short Glass", { exact: true }).waitFor();
  await page.getByText("3", { exact: true }).first().waitFor();

  const desktopOverflow = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 2
  );
  if (desktopOverflow) {
    throw new Error("The desktop application shell has horizontal overflow.");
  }

  await page.getByRole("button", { name: "Open module command palette" }).click();
  await page.getByLabel("Search modules").fill("inventory");
  await page.getByRole("button", { name: /Inventory/ }).click();
  await page.getByRole("heading", { name: "Inventory" }).waitFor();
  await page.getByText("Current inventory", { exact: true }).waitFor();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload({ waitUntil: "networkidle" });
  await page.getByRole("button", { name: "Open navigation" }).waitFor();
  await page.getByRole("button", { name: "Open navigation" }).click();

  const sidebarOpen = await page.locator("#application").evaluate((element) =>
    element.classList.contains("sidebar-open")
  );
  if (!sidebarOpen) {
    throw new Error("Mobile navigation did not open.");
  }

  await page.getByRole("button", { name: "Close navigation" }).click();
  const mobileOverflow = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 2
  );
  if (mobileOverflow) {
    throw new Error("The mobile application shell has horizontal overflow.");
  }

  const unlabeledInteractive = await page.evaluate(() => {
    const elements = [...document.querySelectorAll("button, input, select, textarea, a[href]")];
    return elements.filter((element) => {
      if (element.hidden || element.closest(".hidden")) {
        return false;
      }
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

  if (pageErrors.length) {
    throw new Error(`Browser page errors: ${pageErrors.join(" | ")}`);
  }

  const relevantConsoleErrors = consoleErrors.filter((message) =>
    !message.includes("favicon")
  );
  if (relevantConsoleErrors.length) {
    throw new Error(`Browser console errors: ${relevantConsoleErrors.join(" | ")}`);
  }

  console.log("Nexus operator experience browser validation passed.");
} finally {
  await browser.close();
}
