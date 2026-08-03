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


path = "src/Robo.Pos.Server/Sales/ShopReceiptService.cs"
text = read(path)
first_payment_sql = """            COALESCE(
                (
                    SELECT payment.payment_method
                    FROM sale_payments AS payment
                    WHERE payment.sale_id = sale.id
                    ORDER BY payment.id
                    LIMIT 1
                ),
                ''),"""
split_payment_sql = """            CASE
                WHEN
                (
                    SELECT COUNT(*)
                    FROM sale_payments AS payment
                    WHERE payment.sale_id = sale.id
                ) > 1 THEN 'split'
                ELSE COALESCE(
                    (
                        SELECT payment.payment_method
                        FROM sale_payments AS payment
                        WHERE payment.sale_id = sale.id
                        ORDER BY payment.id
                        LIMIT 1
                    ),
                    '')
            END,"""
if text.count(first_payment_sql) != 1:
    raise RuntimeError(f"receipt payment summary: expected 1 match, found {text.count(first_payment_sql)}")
text = text.replace(first_payment_sql, split_payment_sql)
text = replace_once(
    text,
    "        IReadOnlyList<GeneratedSaleDocument> documents =\n            await ReadOriginalDocumentsAsync(\n                connection,\n                normalizedSaleId,\n                cancellationToken);\n\n        return new ReceiptDetails(",
    "        IReadOnlyList<GeneratedSaleDocument> documents =\n"
    "            await ReadOriginalDocumentsAsync(\n"
    "                connection,\n"
    "                normalizedSaleId,\n"
    "                cancellationToken);\n\n"
    "        IReadOnlyList<CompletedSalePayment> payments =\n"
    "            await ReadPaymentsAsync(\n"
    "                connection,\n"
    "                normalizedSaleId,\n"
    "                cancellationToken);\n\n"
    "        return new ReceiptDetails(",
    "read receipt payments",
)
text = replace_once(
    text,
    "            header.ShopId,\n            header.ShopCode,\n            header.ShopName);",
    "            header.ShopId,\n"
    "            header.ShopCode,\n"
    "            header.ShopName,\n"
    "            payments);",
    "append receipt payments",
)
payment_reader = r'''
    private static async Task<IReadOnlyList<CompletedSalePayment>> ReadPaymentsAsync(
        SqliteConnection connection,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT payment_method, amount_minor, reference
        FROM sale_payments
        WHERE sale_id = $saleId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        var payments = new List<CompletedSalePayment>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            payments.Add(new CompletedSalePayment(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2)));
        }

        return payments;
    }

'''
text = replace_once(
    text,
    "    private static async Task<IReadOnlyList<GeneratedSaleDocument>>\n        ReadOriginalDocumentsAsync(\n",
    payment_reader + "    private static async Task<IReadOnlyList<GeneratedSaleDocument>>\n        ReadOriginalDocumentsAsync(\n",
    "insert receipt payment reader",
)
write(path, text)

path = "src/Robo.Pos.Server/Sales/SalesEndpoints.cs"
text = read(path)
text = replace_once(
    text,
    "                        receipt.Items,\n                        receipt.Documents,\n                        receipt.ShopId,",
    "                        receipt.Items,\n"
    "                        receipt.Documents,\n"
    "                        receipt.Payments,\n"
    "                        receipt.ShopId,",
    "expose receipt payments",
)
write(path, text)

path = "src/Robo.Pos.Server/Sales/SalesReturnService.cs"
text = read(path)
text = replace_once(
    text,
    "        INNER JOIN sale_payments AS payment\n            ON payment.sale_id = sale.id",
    "        INNER JOIN\n"
    "        (\n"
    "            SELECT\n"
    "                sale_id,\n"
    "                MIN(payment_method) AS payment_method\n"
    "            FROM sale_payments\n"
    "            GROUP BY sale_id\n"
    "            HAVING COUNT(*) = 1\n"
    "        ) AS payment\n"
    "            ON payment.sale_id = sale.id",
    "exclude split sales from legacy return list",
)
text = replace_once(
    text,
    "        await using var transaction =\n            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);\n\n        SaleHeader sale = await ReadSaleHeaderAsync(",
    "        await using var transaction =\n"
    "            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);\n\n"
    "        await EnsureSingleTenderReturnAsync(\n"
    "            connection,\n"
    "            transaction,\n"
    "            normalizedSaleId,\n"
    "            cancellationToken);\n\n"
    "        SaleHeader sale = await ReadSaleHeaderAsync(",
    "guard direct split return",
)
return_guard = r'''
    private static async Task EnsureSingleTenderReturnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT COUNT(*)
        FROM sale_payments
        WHERE sale_id = $saleId;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        int paymentCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (paymentCount > 1)
        {
            throw Conflict(
                "split_sale_return_requires_allocation",
                "This sale used multiple payment methods. Use the dedicated split-refund allocation workflow when it is enabled.");
        }
    }

'''
text = regex_once(
    text,
    r"(    private static async Task<SaleHeader> ReadSaleHeaderAsync\()",
    return_guard + r"\1",
    "insert split return guard helper",
)
write(path, text)

path = "src/Robo.Pos.Server/Program.cs"
text = read(path)
if text.count('version = "6.9.0"') != 2:
    raise RuntimeError("Program version: expected two 6.9.0 declarations")
text = text.replace('version = "6.9.0"', 'version = "7.0.0"')
text = replace_once(
    text,
    "        \"immutable-cash-drawer-register\",\n",
    "        \"immutable-cash-drawer-register\",\n"
    "        \"split-and-partial-payments\",\n"
    "        \"cash-change-netting\",\n"
    "        \"payment-reference-audit\",\n"
    "        \"multi-tender-receipt-breakdown\",\n",
    "add split payment capabilities",
)
write(path, text)

path = "src/Robo.Pos.Server/wwwroot/index.html"
text = read(path)
text = replace_once(
    text,
    "  <link rel=\"stylesheet\" href=\"/transactional-workspaces.css\">\n",
    "  <link rel=\"stylesheet\" href=\"/transactional-workspaces.css\">\n"
    "  <link rel=\"stylesheet\" href=\"/split-payments.css\">\n",
    "load split payment css",
)
text = replace_once(
    text,
    "  <script src=\"/transactional-workspaces.js\" defer></script>\n",
    "  <script src=\"/transactional-workspaces.js\" defer></script>\n"
    "  <script src=\"/split-payments.js\" defer></script>\n",
    "load split payment javascript",
)
write(path, text)

print("Patched existing Nexus POS 7.0 files.")
