"use strict";

const APP_VERSION = "2.0.0";
const API_BASE = "";
let state = null;
let currentUser = null;
let currentPage = "dashboard";
let cart = [];
let saveChain = Promise.resolve();
let modalLocked = false;

const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];
const uid = (prefix = "id") => `${prefix}-${Date.now().toString(36)}-${crypto.getRandomValues(new Uint32Array(1))[0].toString(36)}`;
const nowIso = () => new Date().toISOString();
const localDate = (d = new Date()) => {
  const x = new Date(d);
  const y = x.getFullYear(), m = String(x.getMonth()+1).padStart(2,"0"), day = String(x.getDate()).padStart(2,"0");
  return `${y}-${m}-${day}`;
};
const esc = value => String(value ?? "").replace(/[&<>'"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","'":"&#39;",'"':"&quot;"}[c]));
const num = value => Number(value || 0);
const roundMoney = value => Math.round((num(value) + Number.EPSILON) * 100) / 100;
const fmt = value => `${state?.business?.currency || "UGX"} ${Math.round(num(value)).toLocaleString("en-UG")}`;
const fmtQty = value => Number.isInteger(num(value)) ? String(num(value)) : num(value).toFixed(2).replace(/\.00$/,"");
const fmtDateTime = iso => iso ? new Date(iso).toLocaleString("en-UG", {dateStyle:"medium", timeStyle:"short"}) : "—";
const byId = (arr, id) => (arr || []).find(x => x.id === id);

async function api(path, options = {}) {
  const res = await fetch(API_BASE + path, {
    headers: {"Content-Type":"application/json", ...(options.headers || {})},
    ...options
  });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) throw new Error(data?.error || data?.message || `Request failed (${res.status})`);
  return data;
}

async function hashPassword(username, password) {
  const bytes = new TextEncoder().encode(`${String(username).trim().toLowerCase()}|${password}`);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2,"0")).join("");
}

async function createInitialState() {
  const users = [
    {id:"user-baron", username:"baron", displayName:"Baron", role:"admin", passwordHash:await hashPassword("baron","Baron@123"), mustChange:true, active:true, createdAt:nowIso()},
    {id:"user-teller1", username:"teller1", displayName:"Teller One", role:"teller", passwordHash:await hashPassword("teller1","Teller1@123"), mustChange:true, active:true, createdAt:nowIso()},
    {id:"user-teller2", username:"teller2", displayName:"Teller Two", role:"teller", passwordHash:await hashPassword("teller2","Teller2@123"), mustChange:true, active:true, createdAt:nowIso()}
  ];
  return {
    meta:{version:APP_VERSION, createdAt:nowIso(), updatedAt:nowIso()},
    business:{
      name:"ROBO CASK & TAP", owner:"WANZIRA ROBERT",
      address:"Namugongo Road, near TEXOL Fuel, Kampala, Uganda",
      phone1:"+256 771831729", phone2:"+256 700355086", currency:"UGX",
      receiptFooter:"Thank you for shopping with us.", taxRate:0
    },
    users,
    categories:["Spirits","Wines","Beers","Soft Drinks","Short Glass","Other"],
    products:[], sales:[], expenses:[], shifts:[], stockMovements:[], audit:[],
    counters:{sale:0, expense:0, shift:0, product:0}
  };
}

function normalizeState(raw) {
  raw = raw && typeof raw === "object" ? raw : {};
  raw.meta ||= {version:APP_VERSION,createdAt:nowIso(),updatedAt:nowIso()};
  raw.business ||= {};
  raw.business.name ||= "ROBO CASK & TAP";
  raw.business.owner ||= "WANZIRA ROBERT";
  raw.business.address ||= "Namugongo Road, near TEXOL Fuel, Kampala, Uganda";
  raw.business.phone1 ||= "+256 771831729";
  raw.business.phone2 ||= "+256 700355086";
  raw.business.currency ||= "UGX";
  raw.business.receiptFooter ||= "Thank you for shopping with us.";
  raw.business.taxRate = num(raw.business.taxRate);
  for (const k of ["users","categories","products","sales","expenses","shifts","stockMovements","audit"]) raw[k] = Array.isArray(raw[k]) ? raw[k] : [];
  raw.counters ||= {sale:0,expense:0,shift:0,product:0};
  return raw;
}

async function loadState() {
  const response = await api("/api/state");
  if (!response || !response.state || !response.state.meta) {
    state = await createInitialState();
    await persist(true);
  } else state = normalizeState(response.state);
}

function persist(immediate = false) {
  state.meta.updatedAt = nowIso();
  const job = async () => api("/api/state", {method:"POST", body:JSON.stringify({state})});
  if (immediate) return job();
  saveChain = saveChain.then(job, job).catch(err => toast(`Data could not be saved: ${err.message}`,"error"));
  return saveChain;
}

function audit(action, detail) {
  state.audit.unshift({id:uid("audit"), at:nowIso(), userId:currentUser?.id || null, userName:currentUser?.displayName || "System", action, detail});
  if (state.audit.length > 3000) state.audit.length = 3000;
}

function toast(message, type = "success") {
  const host = $("#toastHost");
  const el = document.createElement("div");
  el.className = `toast ${type}`;
  el.textContent = message;
  host.appendChild(el);
  setTimeout(() => el.remove(), 3800);
}

function openModal(html, className = "", locked = false) {
  modalLocked = locked;
  const box = $("#modalBox");
  box.className = `modal-box ${className}`.trim();
  box.innerHTML = html;
  $("#modalBackdrop").classList.remove("hidden");
}
function closeModal(force = false){ if(modalLocked && !force) return; modalLocked=false; $("#modalBackdrop").classList.add("hidden"); $("#modalBox").innerHTML=""; }
function modalShell(title, body, foot = "") {
  return `<div class="modal-head"><h2>${esc(title)}</h2><button data-close-modal>×</button></div><div class="modal-body">${body}</div>${foot?`<div class="modal-foot">${foot}</div>`:""}`;
}

document.addEventListener("click", e => {
  if (e.target.matches("[data-close-modal]")) closeModal();
  if (e.target === $("#modalBackdrop")) closeModal();
});

function activeOpenShift(userId = currentUser?.id) {
  return state.shifts.find(s => s.userId === userId && s.status === "open");
}
function completedSales() { return state.sales.filter(s => s.status === "completed"); }
function saleDate(s){ return localDate(s.createdAt); }
function productStock(product) {
  const source = product.stockSourceId ? byId(state.products, product.stockSourceId) : product;
  return source ? num(source.stockQty) : 0;
}
function productStockLabel(product) {
  const source = product.stockSourceId ? byId(state.products, product.stockSourceId) : product;
  if (!source) return "Missing stock source";
  const label = source.stockUnit || source.unit || "units";
  return `${fmtQty(source.stockQty)} ${label}`;
}
function requiredStock(product, qty) { return num(product.deductQty || 1) * num(qty); }
function isLow(product) {
  if (product.stockSourceId) return false;
  return num(product.stockQty) <= num(product.lowStock);
}
function currentShiftCashSales(shift) {
  return state.sales.filter(s => s.status === "completed" && s.shiftId === shift.id && ["Cash","Mixed"].includes(s.paymentMethod)).reduce((a,s)=>a+num(s.cashReceivedForShift ?? (s.paymentMethod === "Cash" ? s.total : 0)),0);
}
function currentShiftCashExpenses(shift) {
  return state.expenses.filter(x => x.shiftId === shift.id && x.paymentMethod === "Cash").reduce((a,x)=>a+num(x.amount),0);
}
function expectedCash(shift) { return roundMoney(num(shift.openingCash) + currentShiftCashSales(shift) - currentShiftCashExpenses(shift)); }

async function login(username, password) {
  const user = state.users.find(u => u.active && u.username.toLowerCase() === username.trim().toLowerCase());
  if (!user || user.passwordHash !== await hashPassword(user.username,password)) throw new Error("Invalid username or password.");
  currentUser = user;
  sessionStorage.setItem("roboUserId", user.id);
  audit("LOGIN", `${user.displayName} signed in.`);
  await persist();
  showApp();
  if (user.mustChange) showChangePassword(true);
}
function logout() {
  if (currentUser) { audit("LOGOUT", `${currentUser.displayName} signed out.`); persist(); }
  currentUser = null; cart=[]; sessionStorage.removeItem("roboUserId");
  $("#appView").classList.add("hidden"); $("#loginView").classList.remove("hidden");
  $("#loginPassword").value=""; $("#loginUsername").focus();
}

function showApp() {
  $("#loginView").classList.add("hidden"); $("#appView").classList.remove("hidden");
  $("#sideUserName").textContent=currentUser.displayName;
  $("#sideUserRole").textContent=currentUser.role === "admin" ? "ADMINISTRATOR" : "TELLER";
  $("#sideAvatar").textContent=currentUser.displayName.charAt(0).toUpperCase();
  $$('[data-admin]').forEach(el=>el.classList.toggle("hidden", currentUser.role!=="admin"));
  updateBusinessHeader(); updateShiftPill(); navigate("dashboard");
}
function updateBusinessHeader(){
  $("#topBusinessName").textContent=state.business.name;
  $("#topAddress").textContent=state.business.address;
}
function updateShiftPill(){
  const shift=activeOpenShift(); const pill=$("#shiftPill");
  if(shift){pill.textContent=`SHIFT OPEN · ${new Date(shift.openedAt).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})}`;pill.classList.remove("closed");}
  else{pill.textContent="NO OPEN SHIFT";pill.classList.add("closed");}
}

function navigate(page) {
  const adminOnly=["inventory","expenses","reports","users","settings","audit"];
  if(adminOnly.includes(page)&&currentUser.role!=="admin") page="dashboard";
  currentPage=page;
  $$("#sideNav button").forEach(b=>b.classList.toggle("active",b.dataset.page===page));
  const renderers={dashboard:renderDashboard,pos:renderPOS,sales:renderSales,inventory:renderInventory,expenses:renderExpenses,reports:renderReports,shifts:renderShifts,users:renderUsers,settings:renderSettings,audit:renderAudit};
  renderers[page]();
}

function pageTitle(title, subtitle, actions="") { return `<div class="page-title"><div><h1>${esc(title)}</h1><p>${esc(subtitle)}</p></div><div class="page-actions">${actions}</div></div>`; }
function metric(label,value,note,cls=""){return `<div class="metric ${cls}"><div class="label">${esc(label)}</div><div class="value">${esc(value)}</div><div class="note">${esc(note)}</div></div>`}

function renderDashboard(){
  const today=localDate(); const sales=completedSales().filter(s=>saleDate(s)===today);
  const revenue=sales.reduce((a,s)=>a+num(s.total),0); const cash=sales.reduce((a,s)=>a+(["Cash","Mixed"].includes(s.paymentMethod)?num(s.cashReceivedForShift??s.total):0),0);
  const profit=sales.reduce((a,s)=>a+num(s.grossProfit),0); const expenses=state.expenses.filter(x=>localDate(x.createdAt)===today).reduce((a,x)=>a+num(x.amount),0);
  const low=state.products.filter(p=>p.active!==false&&!p.stockSourceId&&isLow(p));
  const recent=[...state.sales].sort((a,b)=>b.createdAt.localeCompare(a.createdAt)).slice(0,7);
  const shift=activeOpenShift();
  $("#pageHost").innerHTML=pageTitle("Business Dashboard","Live sales, cash, profit and stock position.",`<button class="btn primary" id="dashPos">New Sale</button>`) +
    `<div class="metrics">${metric("Today's Sales",fmt(revenue),`${sales.length} completed transactions`)}${metric("Cash Collected",fmt(cash),"Cash and cash part of mixed payments")}${metric("Gross Profit",fmt(profit),"Sales less product cost","success")}${metric("Net After Expenses",fmt(profit-expenses),`Expenses today: ${fmt(expenses)}`,profit-expenses<0?"warning":"success")}</div>
    <div class="grid-2">
      <section class="card"><div class="card-head"><h2>My Shift</h2>${shift?'<span class="status">OPEN</span>':'<span class="status closed">CLOSED</span>'}</div>${shift?`<div class="kpi-band"><div class="mini-kpi"><span>Opening cash</span><strong>${fmt(shift.openingCash)}</strong></div><div class="mini-kpi"><span>Cash sales</span><strong>${fmt(currentShiftCashSales(shift))}</strong></div><div class="mini-kpi"><span>Expected cash</span><strong>${fmt(expectedCash(shift))}</strong></div></div><button class="btn warning" id="dashCloseShift">Close shift</button>`:`<div class="empty">Open a teller shift before making a sale.<br><br><button class="btn primary" id="dashOpenShift">Open Shift</button></div>`}</section>
      <section class="card"><div class="card-head"><h2>Low Stock</h2><button class="btn small" id="dashStock">Manage Stock</button></div>${low.length?`<div class="table-wrap"><table><thead><tr><th>Item</th><th>Available</th><th>Reorder level</th></tr></thead><tbody>${low.slice(0,7).map(p=>`<tr><td><strong>${esc(p.name)}</strong></td><td class="danger-text">${fmtQty(p.stockQty)} ${esc(p.stockUnit||p.unit||"units")}</td><td>${fmtQty(p.lowStock)}</td></tr>`).join("")}</tbody></table></div>`:`<div class="empty">No low-stock items. Add products and reorder levels to activate alerts.</div>`}</section>
    </div>
    <section class="card" style="margin-top:18px"><div class="card-head"><h2>Recent Sales</h2><button class="btn small" id="dashSales">View All</button></div>${recent.length?salesTable(recent):'<div class="empty">No sales yet. Use Point of Sale to record the first transaction.</div>'}</section>`;
  $("#dashPos").onclick=()=>navigate("pos");
  $("#dashSales").onclick=()=>navigate("sales");
  $("#dashStock").onclick=()=>currentUser.role==="admin"?navigate("inventory"):toast("Only Baron can manage stock.","warn");
  $("#dashOpenShift")?.addEventListener("click",showOpenShift);
  $("#dashCloseShift")?.addEventListener("click",showCloseShift);
}

function renderPOS(){
  const products=state.products.filter(p=>p.active!==false&&p.sellable!==false);
  const categories=["All",...new Set(products.map(p=>p.category).filter(Boolean))];
  $("#pageHost").innerHTML=pageTitle("Point of Sale","Select products, receive payment and print an 80 mm receipt.",`<button class="btn" id="posShiftBtn">${activeOpenShift()?"View Shift":"Open Shift"}</button>`) +
  `<div class="pos-layout"><section class="product-panel"><div class="toolbar"><input class="grow" id="productSearch" placeholder="Search by product name or code"><select id="productCategory">${categories.map(c=>`<option>${esc(c)}</option>`).join("")}</select></div><div id="productGrid" class="product-grid"></div></section>
  <aside class="cart-panel"><div class="card-head"><h2>Current Sale</h2><button class="btn small" id="clearCart">Clear</button></div><div id="cartList" class="cart-list"></div><div class="cart-total"><div class="sum-row"><span>Items</span><strong id="cartItems">0</strong></div><div class="sum-row total"><span>Total</span><span id="cartTotal">${fmt(0)}</span></div><button class="btn primary checkout-btn" id="checkoutBtn">Receive Payment</button></div></aside></div>`;
  const drawProducts=()=>{
    const q=$("#productSearch").value.toLowerCase().trim(), cat=$("#productCategory").value;
    const filtered=products.filter(p=>(cat==="All"||p.category===cat)&&(`${p.name} ${p.code}`.toLowerCase().includes(q)));
    $("#productGrid").innerHTML=filtered.length?filtered.map(p=>`<article class="product-card" data-product-id="${esc(p.id)}"><div class="cat">${esc(p.category||"Other")}</div><h3>${esc(p.name)}</h3><div class="price">${fmt(p.price)}</div><div class="stock">Available: ${esc(productStockLabel(p))}</div></article>`).join(""):`<div class="empty" style="grid-column:1/-1">${products.length?"No products match the search.":"Baron must add products before sales can begin."}</div>`;
    $$("[data-product-id]",$("#productGrid")).forEach(el=>el.onclick=()=>addToCart(el.dataset.productId));
  };
  $("#productSearch").oninput=drawProducts; $("#productCategory").onchange=drawProducts; drawProducts(); drawCart();
  $("#clearCart").onclick=()=>{cart=[];drawCart();};
  $("#checkoutBtn").onclick=showPayment;
  $("#posShiftBtn").onclick=()=>activeOpenShift()?navigate("shifts"):showOpenShift();
}
function addToCart(productId){
  const p=byId(state.products,productId); if(!p)return;
  const line=cart.find(x=>x.productId===productId); if(line)line.qty+=1;else cart.push({productId,qty:1});
  drawCart();
}
function drawCart(){
  const list=$("#cartList"); if(!list)return;
  list.innerHTML=cart.length?cart.map(line=>{const p=byId(state.products,line.productId);return `<div class="cart-line"><div><h4>${esc(p?.name||"Missing product")}</h4><div class="line-price">${fmt(p?.price||0)} × ${fmtQty(line.qty)} = <strong>${fmt(num(p?.price)*line.qty)}</strong></div></div><div class="qty-control"><button data-minus="${line.productId}">−</button><input data-qty="${line.productId}" type="number" min="0.01" step="1" value="${line.qty}"><button data-plus="${line.productId}">+</button></div></div>`}).join(""):'<div class="empty">The cart is empty. Select a product to begin.</div>';
  $$('[data-minus]',list).forEach(b=>b.onclick=()=>changeQty(b.dataset.minus,-1)); $$('[data-plus]',list).forEach(b=>b.onclick=()=>changeQty(b.dataset.plus,1));
  $$('[data-qty]',list).forEach(i=>i.onchange=()=>setQty(i.dataset.qty,num(i.value)));
  const total=cart.reduce((a,l)=>a+num(byId(state.products,l.productId)?.price)*l.qty,0);
  $("#cartItems").textContent=fmtQty(cart.reduce((a,l)=>a+l.qty,0)); $("#cartTotal").textContent=fmt(total); $("#checkoutBtn").disabled=!cart.length;
}
function changeQty(id,delta){const l=cart.find(x=>x.productId===id);if(!l)return;l.qty+=delta;if(l.qty<=0)cart=cart.filter(x=>x!==l);drawCart();}
function setQty(id,value){if(value<=0)cart=cart.filter(x=>x.productId!==id);else{const l=cart.find(x=>x.productId===id);if(l)l.qty=value;}drawCart();}

function showPayment(){
  if(!cart.length)return;
  const shift=activeOpenShift(); if(!shift){toast("Open a teller shift before receiving payment.","warn");showOpenShift();return;}
  const total=roundMoney(cart.reduce((a,l)=>a+num(byId(state.products,l.productId)?.price)*l.qty,0));
  openModal(modalShell("Receive Payment",`<div class="metric"><div class="label">Amount Due</div><div class="value">${fmt(total)}</div><div class="note">Confirm payment before completing the sale.</div></div><form id="paymentForm" class="form-grid"><label class="full">Payment method<select id="payMethod"><option>Cash</option><option>Mobile Money</option><option>Card</option><option>Credit</option><option>Mixed</option></select></label><label>Amount received<input id="amountPaid" type="number" min="0" step="1" value="${Math.ceil(total)}"></label><label>Cash portion for mixed payment<input id="cashPortion" type="number" min="0" step="1" value="0" disabled></label><label class="full">Customer / payment note (optional)<input id="payNote" maxlength="120"></label><div id="paymentChange" class="alert info full">Change: ${fmt(0)}</div></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn primary" id="completeSaleBtn">Complete Sale & Print</button>`));
  const method=$("#payMethod"), paid=$("#amountPaid"), cashPortion=$("#cashPortion");
  const update=()=>{cashPortion.disabled=method.value!=="Mixed";if(method.value!=="Cash"&&method.value!=="Mixed") paid.value=total;const change=method.value==="Cash"?Math.max(0,num(paid.value)-total):0;$("#paymentChange").textContent=`Change: ${fmt(change)}`;};
  method.onchange=update;paid.oninput=update;cashPortion.oninput=update;update();
  $("#completeSaleBtn").onclick=()=>completeSale({paymentMethod:method.value,amountPaid:num(paid.value),cashPortion:num(cashPortion.value),note:$("#payNote").value.trim()});
}
async function completeSale(payment){
  const shift=activeOpenShift(); if(!shift) return toast("Your shift is no longer open.","error");
  const lines=[]; let total=0,cost=0;
  const stockRequired = new Map();
  for(const cartLine of cart){
    const p=byId(state.products,cartLine.productId); if(!p||p.active===false) return toast("A product in the cart is unavailable.","error");
    const source=p.stockSourceId?byId(state.products,p.stockSourceId):p; if(!source) return toast(`${p.name} has a missing stock source.`,"error");
    const needed=requiredStock(p,cartLine.qty);
    stockRequired.set(source.id, num(stockRequired.get(source.id)) + needed);
    const lineTotal=roundMoney(num(p.price)*cartLine.qty), lineCost=roundMoney(num(p.cost)*cartLine.qty);
    lines.push({productId:p.id,productCode:p.code,productName:p.name,qty:cartLine.qty,unitPrice:num(p.price),unitCost:num(p.cost),lineTotal,lineCost,stockSourceId:source.id,stockDeducted:needed,unit:p.unit}); total+=lineTotal;cost+=lineCost;
  }
  for(const [sourceId, needed] of stockRequired){
    const source=byId(state.products,sourceId);
    if(!source || num(source.stockQty)+1e-9<needed) return toast(`Insufficient stock for ${source?.name||"a stock source"}. Available: ${fmtQty(source?.stockQty||0)} ${source?.stockUnit||source?.unit||"units"}.`,"error");
  }
  total=roundMoney(total);cost=roundMoney(cost);
  if(payment.paymentMethod==="Cash"&&payment.amountPaid<total) return toast("Amount received is less than the total.","error");
  if(payment.paymentMethod==="Mixed"&&(payment.cashPortion<0||payment.cashPortion>total)) return toast("Enter a valid cash portion for the mixed payment.","error");
  for(const line of lines){const source=byId(state.products,line.stockSourceId);source.stockQty=roundMoney(num(source.stockQty)-line.stockDeducted);state.stockMovements.unshift({id:uid("mov"),at:nowIso(),productId:source.id,productName:source.name,type:"sale",qty:-line.stockDeducted,reason:`Sale of ${line.productName}`,userId:currentUser.id,userName:currentUser.displayName});}
  state.counters.sale=num(state.counters.sale)+1;
  const date=localDate().replaceAll("-","");
  const receiptNo=`RCT-${date}-${String(state.counters.sale).padStart(5,"0")}`;
  const sale={id:uid("sale"),receiptNo,createdAt:nowIso(),userId:currentUser.id,tellerName:currentUser.displayName,shiftId:shift.id,items:lines,total,cost,grossProfit:roundMoney(total-cost),paymentMethod:payment.paymentMethod,amountPaid:payment.paymentMethod==="Cash"?payment.amountPaid:total,cashReceivedForShift:payment.paymentMethod==="Cash"?total:(payment.paymentMethod==="Mixed"?payment.cashPortion:0),change:payment.paymentMethod==="Cash"?roundMoney(payment.amountPaid-total):0,note:payment.note,status:"completed"};
  state.sales.unshift(sale);audit("SALE_COMPLETED",`${receiptNo} completed for ${fmt(total)}.`);await persist();cart=[];closeModal();drawCart();updateShiftPill();showReceipt(sale);toast("Sale completed and stock updated.");
}

function salesTable(sales, actions=true){return `<div class="table-wrap"><table><thead><tr><th>Receipt</th><th>Date</th><th>Teller</th><th>Payment</th><th>Status</th><th>Total</th>${actions?"<th>Action</th>":""}</tr></thead><tbody>${sales.map(s=>`<tr><td><strong>${esc(s.receiptNo)}</strong></td><td>${esc(fmtDateTime(s.createdAt))}</td><td>${esc(s.tellerName)}</td><td>${esc(s.paymentMethod)}</td><td><span class="status ${s.status==='void'?'void':''}">${esc(s.status.toUpperCase())}</span></td><td class="money">${fmt(s.total)}</td>${actions?`<td><div class="inline-actions"><button class="btn small" data-view-sale="${s.id}">View / Print</button>${currentUser.role==='admin'&&s.status==='completed'?`<button class="btn small danger" data-void-sale="${s.id}">Void</button>`:""}</div></td>`:""}</tr>`).join("")}</tbody></table></div>`}
function bindSaleActions(root=document){$$('[data-view-sale]',root).forEach(b=>b.onclick=()=>showReceipt(byId(state.sales,b.dataset.viewSale)));$$('[data-void-sale]',root).forEach(b=>b.onclick=()=>showVoidSale(b.dataset.voidSale));}
function renderSales(){
  $("#pageHost").innerHTML=pageTitle("Sales & Receipts","Search transactions, reprint receipts and review payment records.",`<button class="btn primary" id="salesNew">New Sale</button>`) + `<div class="toolbar"><input class="grow" id="salesSearch" placeholder="Search receipt, teller or payment"><input id="salesDate" type="date"><select id="salesStatus"><option value="all">All status</option><option value="completed">Completed</option><option value="void">Void</option></select></div><div id="salesTableHost"></div>`;
  const draw=()=>{const q=$("#salesSearch").value.toLowerCase(),date=$("#salesDate").value,status=$("#salesStatus").value;const list=state.sales.filter(s=>(!q||`${s.receiptNo} ${s.tellerName} ${s.paymentMethod}`.toLowerCase().includes(q))&&(!date||saleDate(s)===date)&&(status==="all"||s.status===status));$("#salesTableHost").innerHTML=list.length?salesTable(list):'<div class="empty">No matching sales.</div>';bindSaleActions($("#salesTableHost"));};
  $("#salesSearch").oninput=draw;$("#salesDate").onchange=draw;$("#salesStatus").onchange=draw;$("#salesNew").onclick=()=>navigate("pos");draw();
}
function showReceipt(sale){if(!sale)return;const b=state.business;openModal(`<div class="modal-head"><h2>${esc(sale.receiptNo)}</h2><button data-close-modal>×</button></div><div class="receipt-wrap"><div class="receipt-paper"><div class="receipt-center"><h2>${esc(b.name)}</h2><div>${esc(b.address)}</div><div>${esc(b.phone1)}${b.phone2?` / ${esc(b.phone2)}`:""}</div><div class="rule"></div><strong>SALES RECEIPT</strong></div><div class="rule"></div><div>Receipt: ${esc(sale.receiptNo)}</div><div>Date: ${esc(fmtDateTime(sale.createdAt))}</div><div>Served by: ${esc(sale.tellerName)}</div><div>Payment: ${esc(sale.paymentMethod)}</div><div class="rule"></div>${sale.items.map(i=>`<div><strong>${esc(i.productName)}</strong></div><div class="receipt-row"><span>${fmtQty(i.qty)} × ${Math.round(i.unitPrice).toLocaleString("en-UG")}</span><span>${Math.round(i.lineTotal).toLocaleString("en-UG")}</span></div>`).join("")}<div class="rule"></div><div class="receipt-row"><strong>TOTAL</strong><strong>${fmt(sale.total)}</strong></div><div class="receipt-row"><span>Amount paid</span><span>${fmt(sale.amountPaid)}</span></div><div class="receipt-row"><span>Change</span><span>${fmt(sale.change)}</span></div>${sale.status==='void'?'<div class="alert danger receipt-center"><strong>VOID SALE</strong></div>':''}<div class="rule"></div><div class="receipt-footer">${esc(b.receiptFooter)}<br>Internal sales receipt · No verification code<br><br>** END OF RECEIPT **</div></div></div><div class="modal-foot"><button class="btn" data-close-modal>Close</button><button class="btn primary" id="printReceiptBtn">Print Receipt</button></div>`,"receipt-modal");$("#printReceiptBtn").onclick=()=>window.print();}
function showVoidSale(id){const sale=byId(state.sales,id);if(!sale||sale.status!=="completed")return;openModal(modalShell("Void Sale",`<div class="alert danger">Voiding restores the deducted stock and keeps a permanent audit record.</div><p><strong>${esc(sale.receiptNo)}</strong> · ${fmt(sale.total)}</p><form class="form-grid"><label class="full">Reason<input id="voidReason" maxlength="160" required placeholder="Enter the reason for voiding"></label></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn danger" id="confirmVoid">Void Sale</button>`));$("#confirmVoid").onclick=async()=>{const reason=$("#voidReason").value.trim();if(!reason)return toast("Enter a void reason.","warn");for(const line of sale.items){const source=byId(state.products,line.stockSourceId);if(source){source.stockQty=roundMoney(num(source.stockQty)+num(line.stockDeducted));state.stockMovements.unshift({id:uid("mov"),at:nowIso(),productId:source.id,productName:source.name,type:"void",qty:num(line.stockDeducted),reason:`Void ${sale.receiptNo}`,userId:currentUser.id,userName:currentUser.displayName});}}sale.status="void";sale.voidReason=reason;sale.voidedAt=nowIso();sale.voidedBy=currentUser.displayName;audit("SALE_VOIDED",`${sale.receiptNo} voided. Reason: ${reason}`);await persist();closeModal();navigate("sales");toast("Sale voided and stock restored.");};}

function renderInventory(){
  $("#pageHost").innerHTML=pageTitle("Products & Stock","Set prices, costs, stock levels and short-glass stock mappings.",`<button class="btn" id="stockHistoryBtn">Stock History</button><button class="btn primary" id="addProductBtn">Add Product</button>`) + `<div class="toolbar"><input class="grow" id="invSearch" placeholder="Search name, code or category"><select id="invFilter"><option value="all">All products</option><option value="low">Low stock</option><option value="sellable">Sellable</option><option value="stock">Stock sources</option></select></div><div id="inventoryHost"></div>`;
  const draw=()=>{const q=$("#invSearch").value.toLowerCase(),filter=$("#invFilter").value;const list=state.products.filter(p=>(!q||`${p.name} ${p.code} ${p.category}`.toLowerCase().includes(q))&&(filter==="all"||(filter==="low"&&isLow(p))||(filter==="sellable"&&p.sellable!==false)||(filter==="stock"&&!p.stockSourceId)));$("#inventoryHost").innerHTML=list.length?`<div class="table-wrap"><table><thead><tr><th>Code</th><th>Product</th><th>Category</th><th>Sell price</th><th>Cost</th><th>Available stock</th><th>Stock rule</th><th>Action</th></tr></thead><tbody>${list.map(p=>`<tr><td>${esc(p.code)}</td><td><strong>${esc(p.name)}</strong>${p.active===false?'<br><span class="status closed">INACTIVE</span>':''}</td><td>${esc(p.category||"")}</td><td class="money">${fmt(p.price)}</td><td>${fmt(p.cost)}</td><td class="${isLow(p)?'danger-text':''}">${esc(productStockLabel(p))}</td><td>${p.stockSourceId?`Deduct ${fmtQty(p.deductQty)} from ${esc(byId(state.products,p.stockSourceId)?.name||"Missing source")}`:"Own stock"}</td><td><div class="inline-actions"><button class="btn small" data-adjust-product="${p.id}">Adjust</button><button class="btn small" data-edit-product="${p.id}">Edit</button></div></td></tr>`).join("")}</tbody></table></div>`:'<div class="empty">No products yet.<br><br><button class="btn primary" id="emptyAddProduct">Add the First Product</button></div>';$$('[data-edit-product]',$("#inventoryHost")).forEach(b=>b.onclick=()=>showProductForm(b.dataset.editProduct));$$('[data-adjust-product]',$("#inventoryHost")).forEach(b=>b.onclick=()=>showStockAdjust(b.dataset.adjustProduct));$("#emptyAddProduct")?.addEventListener("click",()=>showProductForm());};
  $("#invSearch").oninput=draw;$("#invFilter").onchange=draw;$("#addProductBtn").onclick=()=>showProductForm();$("#stockHistoryBtn").onclick=showStockHistory;draw();
}
function showProductForm(id=null){
  const p=id?byId(state.products,id):{code:"",name:"",category:state.categories[0]||"Other",unit:"item",stockUnit:"units",price:0,cost:0,stockQty:0,lowStock:0,sellable:true,active:true,stockSourceId:"",deductQty:1};
  const sourceOptions=state.products.filter(x=>x.id!==id&&!x.stockSourceId).map(x=>`<option value="${x.id}" ${p.stockSourceId===x.id?'selected':''}>${esc(x.name)} (${esc(x.stockUnit||x.unit||"units")})</option>`).join("");
  openModal(modalShell(id?"Edit Product":"Add Product",`<form id="productForm" class="form-grid"><label>Product code<input id="pCode" value="${esc(p.code)}" required maxlength="30" placeholder="e.g. BW-500"></label><label>Product name<input id="pName" value="${esc(p.name)}" required maxlength="100"></label><label>Category<select id="pCategory">${state.categories.map(c=>`<option ${p.category===c?'selected':''}>${esc(c)}</option>`).join("")}</select></label><label>Sale unit<input id="pUnit" value="${esc(p.unit||"item")}" placeholder="bottle, can, glass"></label><label>Selling price<input id="pPrice" type="number" min="0" step="1" value="${num(p.price)}"></label><label>Cost per sale unit<input id="pCost" type="number" min="0" step="1" value="${num(p.cost)}"></label><label>Own stock quantity<input id="pStock" type="number" step="0.01" value="${num(p.stockQty)}"></label><label>Own stock unit<input id="pStockUnit" value="${esc(p.stockUnit||p.unit||"units")}" placeholder="bottles, cans, ml"></label><label>Low-stock alert level<input id="pLow" type="number" min="0" step="0.01" value="${num(p.lowStock)}"></label><label>Stock source<select id="pSource"><option value="">Use this product's own stock</option>${sourceOptions}</select><span class="form-help">For short glass, select an open-volume stock item measured in ml.</span></label><label>Deduct from source per sale<input id="pDeduct" type="number" min="0.01" step="0.01" value="${num(p.deductQty||1)}"><span class="form-help">Example: enter 50 when one short glass uses 50 ml.</span></label><label class="check-row"><input id="pSellable" type="checkbox" ${p.sellable!==false?'checked':''}> Available on Point of Sale</label><label class="check-row"><input id="pActive" type="checkbox" ${p.active!==false?'checked':''}> Product is active</label><div class="alert info full"><strong>Short-glass setup:</strong> create a non-sellable stock item such as “Waragi Open Stock” with stock unit <strong>ml</strong>. Then create “Waragi Short 50 ml”, select that stock item as its source and set deduction to <strong>50</strong>.</div></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn primary" id="saveProductBtn">Save Product</button>`));
  $("#saveProductBtn").onclick=async()=>{const code=$("#pCode").value.trim().toUpperCase(),name=$("#pName").value.trim();if(!code||!name)return toast("Product code and name are required.","warn");if(state.products.some(x=>x.id!==id&&x.code.toUpperCase()===code))return toast("That product code already exists.","error");const values={code,name,category:$("#pCategory").value,unit:$("#pUnit").value.trim()||"item",price:num($("#pPrice").value),cost:num($("#pCost").value),stockQty:num($("#pStock").value),stockUnit:$("#pStockUnit").value.trim()||"units",lowStock:num($("#pLow").value),stockSourceId:$("#pSource").value,deductQty:num($("#pDeduct").value)||1,sellable:$("#pSellable").checked,active:$("#pActive").checked,updatedAt:nowIso()};if(id)Object.assign(p,values);else{state.counters.product=num(state.counters.product)+1;state.products.push({id:uid("product"),createdAt:nowIso(),...values});}audit(id?"PRODUCT_UPDATED":"PRODUCT_CREATED",`${name} (${code}) ${id?"updated":"created"}.`);await persist();closeModal();renderInventory();toast("Product saved.");};
}
function showStockAdjust(id){const p=byId(state.products,id);if(!p)return;openModal(modalShell("Adjust Stock",`<p><strong>${esc(p.name)}</strong><br>Current own stock: ${fmtQty(p.stockQty)} ${esc(p.stockUnit||p.unit||"units")}</p><form class="form-grid"><label>Adjustment type<select id="adjustType"><option value="add">Add stock</option><option value="remove">Remove stock</option><option value="set">Set exact balance</option></select></label><label>Quantity<input id="adjustQty" type="number" min="0" step="0.01" required></label><label class="full">Reason<select id="adjustReason"><option>Purchase received</option><option>Opening bottle / stock transfer</option><option>Breakage or damage</option><option>Wastage</option><option>Physical stock count correction</option><option>Other adjustment</option></select></label><label class="full">Note<input id="adjustNote" maxlength="160"></label></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn primary" id="saveAdjustBtn">Apply Adjustment</button>`));$("#saveAdjustBtn").onclick=async()=>{const qty=num($("#adjustQty").value),type=$("#adjustType").value;if(qty<0||!Number.isFinite(qty))return toast("Enter a valid quantity.","warn");const old=num(p.stockQty);let next=type==="add"?old+qty:type==="remove"?old-qty:qty;if(next<0)return toast("Stock cannot be reduced below zero.","error");p.stockQty=roundMoney(next);const delta=roundMoney(next-old),reason=$("#adjustReason").value+($("#adjustNote").value.trim()?`: ${$("#adjustNote").value.trim()}`:"");state.stockMovements.unshift({id:uid("mov"),at:nowIso(),productId:p.id,productName:p.name,type:"adjustment",qty:delta,reason,userId:currentUser.id,userName:currentUser.displayName});audit("STOCK_ADJUSTED",`${p.name}: ${fmtQty(old)} to ${fmtQty(next)}. ${reason}`);await persist();closeModal();renderInventory();toast("Stock adjusted.");};}
function showStockHistory(){openModal(modalShell("Stock Movement History",state.stockMovements.length?`<div class="table-wrap"><table><thead><tr><th>Date</th><th>Product</th><th>Movement</th><th>Reason</th><th>User</th></tr></thead><tbody>${state.stockMovements.slice(0,500).map(m=>`<tr><td>${esc(fmtDateTime(m.at))}</td><td>${esc(m.productName)}</td><td class="${num(m.qty)<0?'danger-text':''}">${num(m.qty)>0?'+':''}${fmtQty(m.qty)}</td><td>${esc(m.reason)}</td><td>${esc(m.userName)}</td></tr>`).join("")}</tbody></table></div>`:'<div class="empty">No stock movements yet.</div>',`<button class="btn" data-close-modal>Close</button>`),"");}

function renderExpenses(){
  $("#pageHost").innerHTML=pageTitle("Business Expenses","Record operating costs so net profit remains accurate.",`<button class="btn primary" id="addExpenseBtn">Add Expense</button>`)+`<div class="toolbar"><input id="expenseDate" type="date"><input class="grow" id="expenseSearch" placeholder="Search category, description or recorder"></div><div id="expenseHost"></div>`;
  const draw=()=>{const date=$("#expenseDate").value,q=$("#expenseSearch").value.toLowerCase();const list=state.expenses.filter(x=>(!date||localDate(x.createdAt)===date)&&(!q||`${x.category} ${x.description} ${x.recordedBy}`.toLowerCase().includes(q)));$("#expenseHost").innerHTML=list.length?`<div class="table-wrap"><table><thead><tr><th>Date</th><th>Category</th><th>Description</th><th>Payment</th><th>Recorded by</th><th>Amount</th></tr></thead><tbody>${list.map(x=>`<tr><td>${esc(fmtDateTime(x.createdAt))}</td><td>${esc(x.category)}</td><td>${esc(x.description)}</td><td>${esc(x.paymentMethod)}</td><td>${esc(x.recordedBy)}</td><td class="money">${fmt(x.amount)}</td></tr>`).join("")}</tbody></table></div>`:'<div class="empty">No matching expenses.</div>';};
  $("#expenseDate").onchange=draw;$("#expenseSearch").oninput=draw;$("#addExpenseBtn").onclick=showExpenseForm;draw();
}
function showExpenseForm(){openModal(modalShell("Add Expense",`<form class="form-grid"><label>Category<select id="expenseCategory"><option>Transport</option><option>Utilities</option><option>Rent</option><option>Wages</option><option>Supplies</option><option>Repairs</option><option>Licences</option><option>Other</option></select></label><label>Amount<input id="expenseAmount" type="number" min="0" step="1" required></label><label>Payment method<select id="expensePayment"><option>Cash</option><option>Mobile Money</option><option>Bank</option><option>Other</option></select></label><label class="full">Description<input id="expenseDescription" maxlength="160" required></label></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn primary" id="saveExpenseBtn">Save Expense</button>`));$("#saveExpenseBtn").onclick=async()=>{const amount=num($("#expenseAmount").value),description=$("#expenseDescription").value.trim();if(amount<=0||!description)return toast("Enter the amount and description.","warn");state.counters.expense=num(state.counters.expense)+1;const shift=activeOpenShift();state.expenses.unshift({id:uid("expense"),expenseNo:`EXP-${String(state.counters.expense).padStart(5,"0")}`,createdAt:nowIso(),category:$("#expenseCategory").value,description,amount,paymentMethod:$("#expensePayment").value,userId:currentUser.id,recordedBy:currentUser.displayName,shiftId:shift?.id||null});audit("EXPENSE_RECORDED",`${description}: ${fmt(amount)}.`);await persist();closeModal();renderExpenses();toast("Expense saved.");};}

function renderReports(){
  const first=new Date();first.setDate(1);
  $("#pageHost").innerHTML=pageTitle("Business Reports","Review revenue, profit, expenses, products and teller performance.",`<button class="btn" id="exportSalesBtn">Export Sales CSV</button>`)+`<div class="toolbar"><label>From <input id="reportFrom" type="date" value="${localDate(first)}"></label><label>To <input id="reportTo" type="date" value="${localDate()}"></label><button class="btn primary" id="runReportBtn">Run Report</button></div><div id="reportHost"></div>`;
  const run=()=>{const from=$("#reportFrom").value,to=$("#reportTo").value;const sales=completedSales().filter(s=>(!from||saleDate(s)>=from)&&(!to||saleDate(s)<=to));const expenses=state.expenses.filter(x=>(!from||localDate(x.createdAt)>=from)&&(!to||localDate(x.createdAt)<=to));const revenue=sales.reduce((a,s)=>a+num(s.total),0),cost=sales.reduce((a,s)=>a+num(s.cost),0),gross=revenue-cost,expenseTotal=expenses.reduce((a,x)=>a+num(x.amount),0);const prod=new Map();sales.flatMap(s=>s.items).forEach(i=>{const x=prod.get(i.productName)||{name:i.productName,qty:0,revenue:0,profit:0};x.qty+=num(i.qty);x.revenue+=num(i.lineTotal);x.profit+=num(i.lineTotal)-num(i.lineCost);prod.set(i.productName,x);});const tellers=new Map();sales.forEach(s=>{const x=tellers.get(s.tellerName)||{name:s.tellerName,count:0,revenue:0};x.count++;x.revenue+=num(s.total);tellers.set(s.tellerName,x);});$("#reportHost").innerHTML=`<div class="metrics">${metric("Revenue",fmt(revenue),`${sales.length} transactions`)}${metric("Cost of Goods",fmt(cost),"Recorded product cost")}${metric("Gross Profit",fmt(gross),"Revenue minus cost","success")}${metric("Net Profit",fmt(gross-expenseTotal),`Expenses: ${fmt(expenseTotal)}`,gross-expenseTotal<0?"warning":"success")}</div><div class="grid-2"><section class="card"><div class="card-head"><h2>Top Products</h2></div>${prod.size?`<div class="table-wrap"><table><thead><tr><th>Product</th><th>Qty</th><th>Revenue</th><th>Profit</th></tr></thead><tbody>${[...prod.values()].sort((a,b)=>b.revenue-a.revenue).map(x=>`<tr><td><strong>${esc(x.name)}</strong></td><td>${fmtQty(x.qty)}</td><td>${fmt(x.revenue)}</td><td>${fmt(x.profit)}</td></tr>`).join("")}</tbody></table></div>`:'<div class="empty">No sales in this period.</div>'}</section><section class="card"><div class="card-head"><h2>Teller Performance</h2></div>${tellers.size?`<div class="table-wrap"><table><thead><tr><th>Teller</th><th>Sales</th><th>Revenue</th></tr></thead><tbody>${[...tellers.values()].sort((a,b)=>b.revenue-a.revenue).map(x=>`<tr><td><strong>${esc(x.name)}</strong></td><td>${x.count}</td><td>${fmt(x.revenue)}</td></tr>`).join("")}</tbody></table></div>`:'<div class="empty">No teller activity in this period.</div>'}</section></div>`;};
  $("#runReportBtn").onclick=run;$("#exportSalesBtn").onclick=()=>exportSalesCSV($("#reportFrom").value,$("#reportTo").value);run();
}
function exportSalesCSV(from,to){const rows=[["Receipt","Date","Teller","Payment","Status","Total","Cost","Gross Profit"]];state.sales.filter(s=>(!from||saleDate(s)>=from)&&(!to||saleDate(s)<=to)).forEach(s=>rows.push([s.receiptNo,s.createdAt,s.tellerName,s.paymentMethod,s.status,s.total,s.cost,s.grossProfit]));downloadBlob(`ROBO_Sales_${from||'all'}_${to||'all'}.csv`,rows.map(r=>r.map(v=>`"${String(v??"").replaceAll('"','""')}"`).join(",")).join("\r\n"),"text/csv");}
function downloadBlob(name,content,type){const a=document.createElement("a");a.href=URL.createObjectURL(new Blob([content],{type}));a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}

function renderShifts(){
  const shift=activeOpenShift();const list=currentUser.role==="admin"?state.shifts:state.shifts.filter(s=>s.userId===currentUser.id);
  $("#pageHost").innerHTML=pageTitle("Teller Shifts","Control opening cash, expected cash, counted cash and variances.",shift?`<button class="btn warning" id="shiftActionBtn">Close My Shift</button>`:`<button class="btn primary" id="shiftActionBtn">Open My Shift</button>`)+`${shift?`<section class="card"><div class="card-head"><h2>Current Open Shift</h2><span class="status">OPEN</span></div><div class="kpi-band"><div class="mini-kpi"><span>Opening cash</span><strong>${fmt(shift.openingCash)}</strong></div><div class="mini-kpi"><span>Cash sales</span><strong>${fmt(currentShiftCashSales(shift))}</strong></div><div class="mini-kpi"><span>Expected cash</span><strong>${fmt(expectedCash(shift))}</strong></div></div><p class="muted">Opened ${fmtDateTime(shift.openedAt)}</p></section>`:'<div class="alert warn">No shift is currently open. A shift is required before completing a sale.</div>'}<section class="card" style="margin-top:18px"><div class="card-head"><h2>${currentUser.role==='admin'?'All Shift History':'My Shift History'}</h2></div>${list.length?`<div class="table-wrap"><table><thead><tr><th>Teller</th><th>Opened</th><th>Closed</th><th>Opening</th><th>Expected</th><th>Counted</th><th>Variance</th><th>Status</th></tr></thead><tbody>${list.map(s=>`<tr><td>${esc(s.tellerName)}</td><td>${esc(fmtDateTime(s.openedAt))}</td><td>${esc(fmtDateTime(s.closedAt))}</td><td>${fmt(s.openingCash)}</td><td>${s.status==='closed'?fmt(s.expectedCash):fmt(expectedCash(s))}</td><td>${s.status==='closed'?fmt(s.countedCash):'—'}</td><td class="${num(s.variance)!==0?'danger-text':''}">${s.status==='closed'?fmt(s.variance):'—'}</td><td><span class="status ${s.status==='closed'?'closed':''}">${esc(s.status.toUpperCase())}</span></td></tr>`).join("")}</tbody></table></div>`:'<div class="empty">No shifts recorded yet.</div>'}</section>`;
  $("#shiftActionBtn").onclick=()=>shift?showCloseShift():showOpenShift();
}
function showOpenShift(){if(activeOpenShift())return toast("A shift is already open.","warn");openModal(modalShell("Open Teller Shift",`<p>Count the cash already in the drawer before starting sales.</p><form class="form-grid"><label class="full">Opening cash<input id="openingCash" type="number" min="0" step="1" value="0"></label><label class="full">Opening note<input id="openingNote" maxlength="160" placeholder="Optional"></label></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn primary" id="confirmOpenShift">Open Shift</button>`));$("#confirmOpenShift").onclick=async()=>{state.counters.shift=num(state.counters.shift)+1;state.shifts.unshift({id:uid("shift"),shiftNo:`SHIFT-${String(state.counters.shift).padStart(5,"0")}`,userId:currentUser.id,tellerName:currentUser.displayName,openedAt:nowIso(),openingCash:num($("#openingCash").value),openingNote:$("#openingNote").value.trim(),status:"open"});audit("SHIFT_OPENED",`${currentUser.displayName} opened a shift with ${fmt(num($("#openingCash").value))}.`);await persist();closeModal();updateShiftPill();navigate(currentPage);toast("Shift opened.");};}
function showCloseShift(){const shift=activeOpenShift();if(!shift)return;const expected=expectedCash(shift);openModal(modalShell("Close Teller Shift",`<div class="kpi-band"><div class="mini-kpi"><span>Opening cash</span><strong>${fmt(shift.openingCash)}</strong></div><div class="mini-kpi"><span>Cash sales</span><strong>${fmt(currentShiftCashSales(shift))}</strong></div><div class="mini-kpi"><span>Expected cash</span><strong>${fmt(expected)}</strong></div></div><form class="form-grid"><label class="full">Physically counted cash<input id="countedCash" type="number" min="0" step="1" value="${expected}"></label><label class="full">Closing note<input id="closingNote" maxlength="160"></label><div id="varianceDisplay" class="alert info full">Variance: ${fmt(0)}</div></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn warning" id="confirmCloseShift">Close Shift</button>`));$("#countedCash").oninput=()=>$("#varianceDisplay").textContent=`Variance: ${fmt(num($("#countedCash").value)-expected)}`;$("#confirmCloseShift").onclick=async()=>{shift.closedAt=nowIso();shift.countedCash=num($("#countedCash").value);shift.expectedCash=expected;shift.variance=roundMoney(shift.countedCash-expected);shift.closingNote=$("#closingNote").value.trim();shift.status="closed";audit("SHIFT_CLOSED",`${currentUser.displayName} closed a shift. Variance: ${fmt(shift.variance)}.`);await persist();closeModal();updateShiftPill();navigate("shifts");toast("Shift closed.");};}

function renderUsers(){
  $("#pageHost").innerHTML=pageTitle("Users","Baron controls the two teller accounts and access permissions.",`<button class="btn primary" id="addUserBtn">Add User</button>`)+`<div class="table-wrap"><table><thead><tr><th>Name</th><th>Username</th><th>Role</th><th>Status</th><th>Password</th><th>Action</th></tr></thead><tbody>${state.users.map(u=>`<tr><td><strong>${esc(u.displayName)}</strong></td><td>${esc(u.username)}</td><td>${esc(u.role.toUpperCase())}</td><td><span class="status ${u.active?'':'closed'}">${u.active?'ACTIVE':'INACTIVE'}</span></td><td>${u.mustChange?'Must change':'Set'}</td><td><div class="inline-actions"><button class="btn small" data-edit-user="${u.id}">Edit</button><button class="btn small warning" data-reset-user="${u.id}">Reset Password</button></div></td></tr>`).join("")}</tbody></table></div>`;
  $("#addUserBtn").onclick=()=>showUserForm();$$('[data-edit-user]').forEach(b=>b.onclick=()=>showUserForm(b.dataset.editUser));$$('[data-reset-user]').forEach(b=>b.onclick=()=>showResetPassword(b.dataset.resetUser));
}
function showUserForm(id=null){const u=id?byId(state.users,id):{displayName:"",username:"",role:"teller",active:true};openModal(modalShell(id?"Edit User":"Add User",`<form class="form-grid"><label>Display name<input id="uName" value="${esc(u.displayName)}" required></label><label>Username<input id="uUsername" value="${esc(u.username)}" required ${id?'readonly':''}></label><label>Role<select id="uRole"><option value="teller" ${u.role==='teller'?'selected':''}>Teller</option><option value="admin" ${u.role==='admin'?'selected':''}>Administrator</option></select></label><label class="check-row"><input id="uActive" type="checkbox" ${u.active?'checked':''}> Account is active</label>${id?'':'<label class="full">Temporary password<input id="uPassword" type="password" value="Welcome@123" required></label>'}</form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn primary" id="saveUserBtn">Save User</button>`));$("#saveUserBtn").onclick=async()=>{const displayName=$("#uName").value.trim(),username=$("#uUsername").value.trim().toLowerCase();if(!displayName||!username)return toast("Name and username are required.","warn");if(state.users.some(x=>x.id!==id&&x.username.toLowerCase()===username))return toast("That username already exists.","error");if(id){if(u.id===currentUser.id&&!$("#uActive").checked)return toast("You cannot deactivate your own account.","error");u.displayName=displayName;u.username=username;u.role=$("#uRole").value;u.active=$("#uActive").checked;}else{const pw=$("#uPassword").value;if(pw.length<6)return toast("Temporary password must be at least 6 characters.","warn");state.users.push({id:uid("user"),displayName,username,role:$("#uRole").value,active:$("#uActive").checked,passwordHash:await hashPassword(username,pw),mustChange:true,createdAt:nowIso()});}audit(id?"USER_UPDATED":"USER_CREATED",`${displayName} account ${id?"updated":"created"}.`);await persist();closeModal();renderUsers();toast("User saved.");};}
function showResetPassword(id){const u=byId(state.users,id);openModal(modalShell("Reset Password",`<p>Set a temporary password for <strong>${esc(u.displayName)}</strong>. The user will be required to change it after login.</p><form class="form-grid"><label class="full">Temporary password<input id="resetPassword" type="password" value="Welcome@123"></label></form>`,`<button class="btn" data-close-modal>Cancel</button><button class="btn warning" id="confirmReset">Reset Password</button>`));$("#confirmReset").onclick=async()=>{const pw=$("#resetPassword").value;if(pw.length<6)return toast("Password must be at least 6 characters.","warn");u.passwordHash=await hashPassword(u.username,pw);u.mustChange=true;audit("PASSWORD_RESET",`${u.displayName}'s password was reset.`);await persist();closeModal();toast("Password reset.");};}
function showChangePassword(forced=false){openModal(modalShell(forced?"Change Temporary Password":"Change Password",`${forced?'<div class="alert warn">You must replace the temporary password before continuing.</div>':''}<form class="form-grid"><label class="full">Current password<input id="oldPassword" type="password"></label><label>New password<input id="newPassword" type="password"></label><label>Confirm new password<input id="confirmPassword" type="password"></label></form>`,`${forced?'':'<button class="btn" data-close-modal>Cancel</button>'}<button class="btn primary" id="savePasswordBtn">Change Password</button>`),"",forced);$("#savePasswordBtn").onclick=async()=>{const old=$("#oldPassword").value,nw=$("#newPassword").value,confirm=$("#confirmPassword").value;if(currentUser.passwordHash!==await hashPassword(currentUser.username,old))return toast("Current password is incorrect.","error");if(nw.length<8)return toast("Use at least 8 characters.","warn");if(nw!==confirm)return toast("New passwords do not match.","warn");currentUser.passwordHash=await hashPassword(currentUser.username,nw);currentUser.mustChange=false;audit("PASSWORD_CHANGED",`${currentUser.displayName} changed their password.`);await persist();closeModal(true);toast("Password changed.");};}

function renderSettings(){const b=state.business;$("#pageHost").innerHTML=pageTitle("Settings & Backup","Update receipt details and protect the business records.",`<button class="btn" id="changeMyPassword">Change My Password</button>`)+`<div class="grid-2"><section class="card"><div class="card-head"><h2>Business & Receipt Details</h2></div><form id="settingsForm" class="form-grid"><label class="full">Business name<input id="sName" value="${esc(b.name)}"></label><label class="full">Owner<input id="sOwner" value="${esc(b.owner)}"></label><label class="full">Address<input id="sAddress" value="${esc(b.address)}"></label><label>Contact 1<input id="sPhone1" value="${esc(b.phone1)}"></label><label>Contact 2<input id="sPhone2" value="${esc(b.phone2)}"></label><label>Currency<input id="sCurrency" value="${esc(b.currency)}"></label><label>Tax rate (%)<input id="sTax" type="number" min="0" step="0.01" value="${num(b.taxRate)}"></label><label class="full">Receipt footer<input id="sFooter" value="${esc(b.receiptFooter)}"></label><div class="full"><button class="btn primary" id="saveSettingsBtn" type="button">Save Settings</button></div></form></section><section class="card"><div class="card-head"><h2>Backup & Recovery</h2></div><div class="alert info">The system stores its main data file on this computer. Download a backup regularly and copy it to a flash drive or Google Drive.</div><div class="soft-box"><strong>Last updated</strong><p>${esc(fmtDateTime(state.meta.updatedAt))}</p><div class="inline-actions"><button class="btn primary" id="downloadBackupBtn">Download Backup</button><label class="btn" for="restoreFile">Restore Backup</label><input id="restoreFile" type="file" accept="application/json" class="hidden"></div></div><div class="soft-box" style="margin-top:14px"><strong>Server Backup Copy</strong><p class="muted">Creates an extra timestamped copy inside the installed data folder.</p><button class="btn" id="serverBackupBtn">Create Server Backup</button></div><div class="alert warn">Do not uninstall Microsoft Edge or delete browser/application data while the business is open. Always keep recent backup files.</div></section></div>`;
  $("#changeMyPassword").onclick=()=>showChangePassword(false);$("#saveSettingsBtn").onclick=async()=>{Object.assign(b,{name:$("#sName").value.trim()||"ROBO CASK & TAP",owner:$("#sOwner").value.trim(),address:$("#sAddress").value.trim(),phone1:$("#sPhone1").value.trim(),phone2:$("#sPhone2").value.trim(),currency:$("#sCurrency").value.trim()||"UGX",taxRate:num($("#sTax").value),receiptFooter:$("#sFooter").value.trim()});audit("SETTINGS_UPDATED","Business and receipt settings updated.");await persist();updateBusinessHeader();renderSettings();toast("Settings saved.");};$("#downloadBackupBtn").onclick=()=>{const copy=JSON.parse(JSON.stringify(state));delete copy.runtime;downloadBlob(`ROBO_CASK_TAP_BACKUP_${localDate()}_${new Date().toTimeString().slice(0,8).replaceAll(':','')}.json`,JSON.stringify(copy,null,2),"application/json");audit("BACKUP_DOWNLOADED","A manual JSON backup was downloaded.");persist();};$("#restoreFile").onchange=async e=>{const file=e.target.files[0];if(!file)return;try{const incoming=normalizeState(JSON.parse(await file.text()));if(!incoming.meta||!Array.isArray(incoming.users)||!Array.isArray(incoming.products))throw new Error("Invalid backup structure");if(!confirm("Restore this backup? Current data will be replaced after an automatic server backup is created."))return;await api("/api/backup",{method:"POST",body:"{}"});state=incoming;audit("BACKUP_RESTORED",`Backup ${file.name} restored.`);await persist(true);closeModal();toast("Backup restored. Please sign in again.");logout();}catch(err){toast(`Restore failed: ${err.message}`,"error");}};$("#serverBackupBtn").onclick=async()=>{try{const r=await api("/api/backup",{method:"POST",body:"{}"});audit("SERVER_BACKUP_CREATED",`Server backup created: ${r.fileName||"backup"}.`);await persist();toast(`Backup created: ${r.fileName||"done"}`);}catch(err){toast(err.message,"error");}};
}

function renderAudit(){const list=state.audit.slice(0,1000);$("#pageHost").innerHTML=pageTitle("Audit Log","Permanent record of important sales, stock, user and settings actions.")+`${list.length?`<div class="table-wrap"><table><thead><tr><th>Date</th><th>User</th><th>Action</th><th>Details</th></tr></thead><tbody>${list.map(a=>`<tr><td>${esc(fmtDateTime(a.at))}</td><td>${esc(a.userName)}</td><td><strong>${esc(a.action)}</strong></td><td>${esc(a.detail)}</td></tr>`).join("")}</tbody></table></div>`:'<div class="empty">No audit entries yet.</div>'}`;}

$("#loginForm").addEventListener("submit",async e=>{e.preventDefault();try{await login($("#loginUsername").value,$("#loginPassword").value);}catch(err){toast(err.message,"error");}});
$("#logoutBtn").onclick=logout;$("#quickPosBtn").onclick=()=>navigate("pos");
$("#sideNav").addEventListener("click",e=>{const btn=e.target.closest("button[data-page]");if(btn)navigate(btn.dataset.page);});

window.addEventListener("error",e=>toast(`Unexpected error: ${e.message}`,"error"));
window.addEventListener("unhandledrejection",e=>toast(`Operation failed: ${e.reason?.message||e.reason}`,"error"));

(async function boot(){
  try{
    await loadState();
    const saved=sessionStorage.getItem("roboUserId");
    const user=byId(state.users,saved);
    if(user&&user.active){currentUser=user;showApp();if(user.mustChange)showChangePassword(true);}else $("#loginView").classList.remove("hidden");
  }catch(err){
    document.body.innerHTML=`<div style="font-family:Segoe UI;padding:40px"><h1>ROBO CASK & TAP could not start</h1><p>${esc(err.message)}</p><p>Run the Repair and Diagnose shortcut from the installation folder.</p></div>`;
  }
})();
