from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str, flags=0) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one regex match, found {count}")
    return updated


path = "src/Robo.Pos.Server/Sales/SalesModels.cs"
text = read(path)
text = replace_once(
    text,
    "public sealed record SaleLineRequest(\n    string ProductId,\n    long Quantity);\n\n",
    "public sealed record SaleLineRequest(\n    string ProductId,\n    long Quantity);\n\n"
    "public sealed record SalePaymentRequest(\n"
    "    string PaymentMethod,\n"
    "    long AmountMinor,\n"
    "    string? Reference = null);\n\n"
    "public sealed record CompletedSalePayment(\n"
    "    string PaymentMethod,\n"
    "    long AmountMinor,\n"
    "    string Reference);\n\n",
    "insert payment records",
)
text = replace_once(
    text,
    "    string? Notes = null,\n    string? CustomerId = null);",
    "    string? Notes = null,\n"
    "    string? CustomerId = null,\n"
    "    IReadOnlyList<SalePaymentRequest>? Payments = null);",
    "extend complete sale request",
)
text = replace_once(
    text,
    "    string? ShopId = null,\n    string? ShopCode = null,\n    string? ShopName = null);\n\npublic sealed record ReceiptListItem",
    "    string? ShopId = null,\n"
    "    string? ShopCode = null,\n"
    "    string? ShopName = null,\n"
    "    IReadOnlyList<CompletedSalePayment>? Payments = null);\n\n"
    "public sealed record ReceiptListItem",
    "extend complete sale result",
)
text = replace_once(
    text,
    "    string? ShopId = null,\n    string? ShopCode = null,\n    string? ShopName = null);\n\npublic sealed record StoredDocumentFile",
    "    string? ShopId = null,\n"
    "    string? ShopCode = null,\n"
    "    string? ShopName = null,\n"
    "    IReadOnlyList<CompletedSalePayment>? Payments = null);\n\n"
    "public sealed record StoredDocumentFile",
    "extend receipt details",
)
write(path, text)

path = "src/Robo.Pos.Server/Sales/AuditDocumentWriter.cs"
text = read(path)
text = replace_once(
    text,
    "public sealed record AuditDocumentLine(\n    string ProductName,\n    string Sku,\n    long Quantity,\n    string SaleUnit,\n    int? UnitSizeMl,\n    long UnitPriceMinor,\n    long LineTotalMinor);\n\n",
    "public sealed record AuditDocumentLine(\n"
    "    string ProductName,\n"
    "    string Sku,\n"
    "    long Quantity,\n"
    "    string SaleUnit,\n"
    "    int? UnitSizeMl,\n"
    "    long UnitPriceMinor,\n"
    "    long LineTotalMinor);\n\n"
    "public sealed record AuditDocumentPayment(\n"
    "    string PaymentMethod,\n"
    "    long AmountMinor,\n"
    "    string Reference);\n\n",
    "insert document payment record",
)
text = replace_once(
    text,
    "    string Notes,\n    DateTimeOffset CompletedAtUtc,\n    IReadOnlyList<AuditDocumentLine> Items);",
    "    string Notes,\n"
    "    DateTimeOffset CompletedAtUtc,\n"
    "    IReadOnlyList<AuditDocumentLine> Items,\n"
    "    IReadOnlyList<AuditDocumentPayment>? Payments = null);",
    "extend document snapshot",
)
old_html = """        html.Append("</div><p>Payment: <strong>");
        html.Append(
            Encode(
                DisplayPaymentMethod(
                    snapshot.PaymentMethod)));
        html.Append("</strong></p>");
"""
new_html = """        html.Append("</div><p>Payment: <strong>");
        html.Append(
            Encode(
                DisplayPaymentMethod(
                    snapshot.PaymentMethod)));
        html.Append("</strong></p>");

        if (snapshot.Payments is { Count: > 0 })
        {
            html.Append("<div class=\"customer\"><strong>Payment breakdown</strong>");
            foreach (AuditDocumentPayment payment in snapshot.Payments)
            {
                html.Append("<p>");
                html.Append(Encode(DisplayPaymentMethod(payment.PaymentMethod)));
                html.Append(": <strong>");
                html.Append(Encode(Money(payment.AmountMinor, snapshot.CurrencyCode)));
                html.Append("</strong>");
                if (!string.IsNullOrWhiteSpace(payment.Reference))
                {
                    html.Append(" · Ref: ");
                    html.Append(Encode(payment.Reference));
                }
                html.Append("</p>");
            }
            html.Append("</div>");
        }
"""
text = replace_once(text, old_html, new_html, "add html payment breakdown")
old_pdf = """        lines.Add(
            $"Payment: {DisplayPaymentMethod(snapshot.PaymentMethod)}");

        if (!string.IsNullOrWhiteSpace(
                snapshot.Notes))
"""
new_pdf = """        lines.Add(
            $"Payment: {DisplayPaymentMethod(snapshot.PaymentMethod)}");

        if (snapshot.Payments is { Count: > 0 })
        {
            lines.Add("Payment breakdown:");
            foreach (AuditDocumentPayment payment in snapshot.Payments)
            {
                string reference = string.IsNullOrWhiteSpace(payment.Reference)
                    ? string.Empty
                    : $" | Ref: {payment.Reference}";
                lines.Add(
                    $"  {DisplayPaymentMethod(payment.PaymentMethod)}: " +
                    $"{Money(payment.AmountMinor, snapshot.CurrencyCode)}{reference}");
            }
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.Notes))
"""
text = replace_once(text, old_pdf, new_pdf, "add pdf payment breakdown")
write(path, text)

path = "src/Robo.Pos.Server/Sales/ShopSaleCompletionService.cs"
text = read(path)
text = regex_once(
    text,
    r"\n        string paymentMethod =\n            request\.PaymentMethod\?\.Trim\(\)\.ToLowerInvariant\(\)\n            \?\? string\.Empty;\n\n        if \(!PaymentMethods\.Contains\(paymentMethod\)\)\n        \{.*?\n        ValidateCustomer\(request, paymentMethod\);\n",
    "\n",
    "remove legacy upfront payment validation",
    flags=re.S,
)
text = regex_once(
    text,
    r"        long total = subtotal;\n        if \(paymentMethod == \"cash\"\)\n        \{.*?\n        long change = paymentMethod == \"cash\"\n            \? request\.AmountReceivedMinor - total\n            : 0;",
    "        long total = subtotal;\n"
    "        PaymentPlan paymentPlan = NormalizePayments(request, total);\n"
    "        ValidateCustomer(request, paymentPlan.HasCredit);\n"
    "        long change = paymentPlan.ChangeMinor;",
    "replace single-payment validation",
    flags=re.S,
)
text = replace_once(
    text,
    "            subtotal,\n            total,\n            change,\n            now,",
    "            subtotal,\n"
    "            total,\n"
    "            paymentPlan.AmountTenderedMinor,\n"
    "            change,\n"
    "            now,",
    "pass tendered amount into sale insert",
)
old_insert_call = """        await InsertPaymentAsync(
            connection,
            transaction,
            saleId,
            paymentMethod,
            total,
            now,
            cancellationToken);
"""
new_insert_call = """        foreach (NormalizedPayment payment in paymentPlan.AppliedPayments)
        {
            await InsertPaymentAsync(
                connection,
                transaction,
                saleId,
                payment.PaymentMethod,
                payment.AmountMinor,
                payment.Reference,
                now,
                cancellationToken);
        }
"""
text = replace_once(text, old_insert_call, new_insert_call, "insert multiple payments")
text = replace_once(
    text,
    "                paymentMethod,\n                totalMinor = total,\n                itemCount = lines.Count",
    "                paymentMethod = paymentPlan.Summary,\n"
    "                amountTenderedMinor = paymentPlan.AmountTenderedMinor,\n"
    "                changeMinor = paymentPlan.ChangeMinor,\n"
    "                payments = paymentPlan.AppliedPayments.Select(payment => new\n"
    "                {\n"
    "                    payment.PaymentMethod,\n"
    "                    payment.AmountMinor,\n"
    "                    payment.Reference\n"
    "                }),\n"
    "                totalMinor = total,\n"
    "                itemCount = lines.Count",
    "audit payment breakdown",
)
text = replace_once(
    text,
    "            paymentMethod,\n            subtotal,",
    "            paymentPlan.Summary,\n            subtotal,",
    "document payment summary",
)
text = replace_once(
    text,
    "            request.AmountReceivedMinor,\n            change,\n            request.Notes?.Trim() ?? string.Empty,\n            now,\n            lines.Select(line => new AuditDocumentLine(\n                    line.ProductName,\n                    line.Sku,\n                    line.Quantity,\n                    line.SaleUnit,\n                    line.UnitSizeMl,\n                    line.UnitPriceMinor,\n                    line.LineTotalMinor))\n                .ToList());",
    "            paymentPlan.AmountTenderedMinor,\n"
    "            change,\n"
    "            request.Notes?.Trim() ?? string.Empty,\n"
    "            now,\n"
    "            lines.Select(line => new AuditDocumentLine(\n"
    "                    line.ProductName,\n"
    "                    line.Sku,\n"
    "                    line.Quantity,\n"
    "                    line.SaleUnit,\n"
    "                    line.UnitSizeMl,\n"
    "                    line.UnitPriceMinor,\n"
    "                    line.LineTotalMinor))\n"
    "                .ToList(),\n"
    "            paymentPlan.AppliedPayments.Select(payment => new AuditDocumentPayment(\n"
    "                    payment.PaymentMethod,\n"
    "                    payment.AmountMinor,\n"
    "                    payment.Reference))\n"
    "                .ToList());",
    "document tendered amount and payments",
)
text = replace_once(
    text,
    "            request.AmountReceivedMinor,\n            change,\n            paymentMethod,\n            now,",
    "            paymentPlan.AmountTenderedMinor,\n"
    "            change,\n"
    "            paymentPlan.Summary,\n"
    "            now,",
    "result payment values",
)
text = replace_once(
    text,
    "            context.ShopId,\n            context.ShopCode,\n            context.ShopName);",
    "            context.ShopId,\n"
    "            context.ShopCode,\n"
    "            context.ShopName,\n"
    "            paymentPlan.AppliedPayments.Select(payment => new CompletedSalePayment(\n"
    "                    payment.PaymentMethod,\n"
    "                    payment.AmountMinor,\n"
    "                    payment.Reference))\n"
    "                .ToList());",
    "append result payments",
)
helper = r'''
    private static PaymentPlan NormalizePayments(
        CompleteSaleRequest request,
        long totalMinor)
    {
        IReadOnlyList<SalePaymentRequest> requested =
            request.Payments is { Count: > 0 }
                ? request.Payments
                : new[]
                {
                    new SalePaymentRequest(
                        request.PaymentMethod,
                        request.AmountReceivedMinor)
                };

        if (requested.Count > 5)
        {
            throw Validation(
                "too_many_payment_methods",
                "A sale cannot use more than five payment methods.");
        }

        var normalized = new List<NormalizedPayment>(requested.Count);
        var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SalePaymentRequest payment in requested)
        {
            string method = payment.PaymentMethod?.Trim().ToLowerInvariant()
                ?? string.Empty;
            if (!PaymentMethods.Contains(method))
            {
                throw Validation(
                    "invalid_payment_method",
                    "Use cash, mobile money, card, bank or credit.");
            }
            if (!methods.Add(method))
            {
                throw Validation(
                    "duplicate_payment_method",
                    "Use each payment method only once in a split payment.");
            }
            if (payment.AmountMinor <= 0)
            {
                throw Validation(
                    "invalid_payment_amount",
                    "Every payment amount must be greater than zero.");
            }

            string reference = payment.Reference?.Trim() ?? string.Empty;
            if (reference.Length > 120)
            {
                throw Validation(
                    "payment_reference_too_long",
                    "A payment reference cannot exceed 120 characters.");
            }

            normalized.Add(new NormalizedPayment(method, payment.AmountMinor, reference));
        }

        bool hasCredit = normalized.Any(payment => payment.PaymentMethod == "credit");
        if (hasCredit && normalized.Count > 1)
        {
            throw Validation(
                "mixed_credit_payment_not_supported",
                "Credit cannot be combined with another tender in this release. Complete a full credit sale or use non-credit split payments.");
        }

        long tendered = normalized.Aggregate(
            0L,
            (sum, payment) => checked(sum + payment.AmountMinor));
        if (tendered < totalMinor)
        {
            throw Validation(
                "insufficient_payment",
                "The combined payment amount is less than the sale total.");
        }

        long change = checked(tendered - totalMinor);
        if (change > 0)
        {
            int cashIndex = normalized.FindIndex(payment => payment.PaymentMethod == "cash");
            if (cashIndex < 0)
            {
                throw Validation(
                    "non_cash_overpayment",
                    "Only cash can exceed the remaining sale balance because change must be returned from the drawer.");
            }

            NormalizedPayment cash = normalized[cashIndex];
            long appliedCash = checked(cash.AmountMinor - change);
            if (appliedCash <= 0)
            {
                throw Validation(
                    "cash_change_exceeds_cash_tender",
                    "Cash change cannot exceed the cash amount tendered.");
            }
            normalized[cashIndex] = cash with { AmountMinor = appliedCash };
        }

        long applied = normalized.Aggregate(
            0L,
            (sum, payment) => checked(sum + payment.AmountMinor));
        if (applied != totalMinor)
        {
            throw new SalesException(
                StatusCodes.Status500InternalServerError,
                "payment_plan_not_balanced",
                "The payment plan did not reconcile to the sale total.");
        }

        string summary = normalized.Count == 1
            ? normalized[0].PaymentMethod
            : "split";

        return new PaymentPlan(
            normalized,
            tendered,
            change,
            summary,
            hasCredit);
    }

'''
text = replace_once(
    text,
    "    private static IReadOnlyList<SaleLineRequest> NormalizeLines(\n",
    helper + "    private static IReadOnlyList<SaleLineRequest> NormalizeLines(\n",
    "insert payment normalization helper",
)
text = replace_once(
    text,
    "    private static void ValidateCustomer(\n        CompleteSaleRequest request,\n        string paymentMethod)",
    "    private static void ValidateCustomer(\n"
    "        CompleteSaleRequest request,\n"
    "        bool hasCredit)",
    "change customer validation signature",
)
text = replace_once(
    text,
    "        if (paymentMethod == \"credit\" &&\n",
    "        if (hasCredit &&\n",
    "validate credit customer",
)
text = replace_once(
    text,
    "        long total,\n        long change,",
    "        long total,\n        long amountReceived,\n        long change,",
    "extend sale insert signature",
)
text = replace_once(
    text,
    "            request.AmountReceivedMinor);",
    "            amountReceived);",
    "store total tendered amount",
)
text = replace_once(
    text,
    "        string paymentMethod,\n        long total,\n        DateTimeOffset now,",
    "        string paymentMethod,\n"
    "        long amountMinor,\n"
    "        string reference,\n"
    "        DateTimeOffset now,",
    "extend payment insert signature",
)
text = replace_once(
    text,
    "        payment.Parameters.AddWithValue(\"$amount\", total);",
    "        payment.Parameters.AddWithValue(\"$amount\", amountMinor);",
    "insert exact payment amount",
)
text = replace_once(
    text,
    "        payment.Parameters.AddWithValue(\"$receivedAtUtc\", now.ToString(\"O\"));",
    "        payment.Parameters.AddWithValue(\"$reference\", reference);\n"
    "        payment.Parameters.AddWithValue(\"$receivedAtUtc\", now.ToString(\"O\"));",
    "bind payment reference",
)
text = replace_once(
    text,
    "            '',\n            $receivedAtUtc",
    "            $reference,\n            $receivedAtUtc",
    "store payment reference",
)
text = replace_once(
    text,
    "    private sealed record BusinessSnapshot(\n",
    "    private sealed record NormalizedPayment(\n"
    "        string PaymentMethod,\n"
    "        long AmountMinor,\n"
    "        string Reference);\n\n"
    "    private sealed record PaymentPlan(\n"
    "        IReadOnlyList<NormalizedPayment> AppliedPayments,\n"
    "        long AmountTenderedMinor,\n"
    "        long ChangeMinor,\n"
    "        string Summary,\n"
    "        bool HasCredit);\n\n"
    "    private sealed record BusinessSnapshot(\n",
    "insert internal payment records",
)
write(path, text)

print("Patched split-payment core.")
