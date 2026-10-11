using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Greptile PR #20 W1 (P1): ten sam Idempotency-Key na innej operacji dawał replay CUDZEJ odpowiedzi - PATCH /receipts/{id} kluczem
/// użytym wcześniej w POST /receipts dostawał zcache'owane InvoiceResponseDto (częściowy ReceiptDetailDto) i 200 bez zapisu numeru.
/// Reguła klasowa (v0.21.0): klucz żyje w przestrzeni operacji = metoda + ścieżka z kanonicznym id zasobu (<see cref="IdempotencyScope"/>);
/// ta sama wartość klucza na innym endpoincie albo innym dokumencie = osobny wpis.
/// </summary>
[Collection("FakeSferaSharedState")]
public class IdempotencyScopeTests
{
    public IdempotencyScopeTests()
    {
        FakeSferaSession.ResetReceiptsForTests();
    }

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_scope_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static (IdempotencyStore store, string path) NewStoreWithPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"idem_scope_{Guid.NewGuid():N}.db");
        return (new IdempotencyStore(new BridgeOptions { IdempotencyStorePath = path, IdempotencyTtlDays = 30 }, NullLogger<IdempotencyStore>.Instance), path);
    }

    /// <summary>Wiersz w formacie sprzed v0.21.0: klucz bez zakresu (tak zapisywał SaveAsync(key, ...) do v0.20.0).</summary>
    private static void InsertUnscoped(string path, string key, object response)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO idempotency (key, response_json, created_at) VALUES ($k, $j, $c)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(response));
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static bool RowExists(string path, string storageKey)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM idempotency WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", storageKey);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static InvoiceResponseDto Doc(long subiektId)
        => new($"sub_{subiektId}", subiektId, $"PZ {subiektId}/2026", DateTimeOffset.UtcNow, 0, new InvoiceTotalsDto(Gross: 0m), null, null);

    /// <summary>Wszystkie operacje z Idempotency-Key (census: 6 POST sprzed v0.21.0 + PATCH z v0.21.0).</summary>
    private static readonly (IdempotencyScope Scope, bool PreExisting)[] AllOperations =
    {
        (IdempotencyScope.CreateInvoice, true),
        (IdempotencyScope.CreateCorrection(1), true),
        (IdempotencyScope.CreateReceipt, true),
        (IdempotencyScope.CreateTransfer, true),
        (IdempotencyScope.CreateSettlement(1), true),
        (IdempotencyScope.BookBankTransaction(1), true),
        (IdempotencyScope.UpdateReceiptHeader(1), false),
    };

    private static ContractorDto Contractor() => new(
        IsPerson: true, Symbol: "K1", Nip: null, Name: "Test Klient", FullName: null, FirstName: null,
        LastName: null, Email: null, Address: new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL"));

    private static InvoiceRequestDto Invoice() => new(
        Type: "FS", IssueDate: "", SaleDate: "",
        Payment: new PaymentDto("PlatnoscPrzelew", null, 100m, true),
        Currency: "PLN", Contractor: Contractor(),
        Lines: new[] { new LineDto("5901234123457", "Towar testowy", 1m, "szt.", 100m, 23m) },
        Shipping: new ShippingDto(false, "Wysyłka", 0m, 23m),
        Totals: new InvoiceTotalsDto(Gross: 100m),
        ExternalReference: $"test:{Guid.NewGuid():N}", Notes: "");

    private static InvoiceCorrectionRequestDto Correction() => new("", "zwrot", false, null, null,
        new[] { new CorrectionLineDto("5901234123457", "Towar", -1m, "szt.", 100m, 23m) }, $"test:kfs:{Guid.NewGuid():N}");

    private static ReceiptIssueRequestDto Receipt() => new("", null, Contractor(),
        new[] { new LineDto("5901234123457", "Towar", 1m, "szt.", 100m, 23m) }, $"test:pz:{Guid.NewGuid():N}", "", "Client:test_buyer");

    private static ReceiptHeaderPatchDto Patch(string value)
        => new(JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone(), null);

    // ----------------------------- W1: kontrolery (RED przed poprawką) -----------------------------

    [Fact]
    public async Task W1_PatchWithKeyUsedForReceiptCreate_ExecutesPatch_NotReplayOfCreate()
    {
        // Klient wg reguły „jeden klucz na (zamówienie × typ dokumentu)": ten sam klucz dla POST /receipts i PATCH tego PZ.
        // Przed poprawką: PATCH czytał wpis POST (InvoiceResponseDto → częściowy ReceiptDetailDto), dokument istnieje → 200 bez zapisu.
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = new ReceiptsController(fake, store, NullLogger<ReceiptsController>.Instance);
        const string key = "test:order:1:PZ";

        var (sCreate, vCreate) = Unwrap((await c.Create(Receipt(), key, CancellationToken.None)).Result);
        Assert.Equal(201, sCreate);
        long id = Assert.IsType<InvoiceResponseDto>(vCreate).SubiektId;

        var (sPatch, vPatch) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("FZ 1/01/2026"), key, CancellationToken.None)).Result);
        Assert.Equal(200, sPatch);
        var dto = Assert.IsType<ReceiptDetailDto>(vPatch);
        Assert.Equal(id, dto.SubiektId);
        Assert.Equal("FZ 1/01/2026", dto.OriginalNumber);
        Assert.NotNull(dto.Revision);
        Assert.Equal("FZ 1/01/2026", FakeSferaSession.OriginalNumberForTests(id));
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));

        // Oba wpisy żyją obok siebie: replay PATCH = DTO edycji bez drugiego zapisu, replay POST = odpowiedź tworzenia.
        var (sPatch2, vPatch2) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("FZ 1/01/2026"), key, CancellationToken.None)).Result);
        Assert.Equal(200, sPatch2);
        Assert.Equal("FZ 1/01/2026", Assert.IsType<ReceiptDetailDto>(vPatch2).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        var (sCreate2, vCreate2) = Unwrap((await c.Create(Receipt(), key, CancellationToken.None)).Result);
        Assert.Equal(200, sCreate2);
        Assert.Equal(id, Assert.IsType<InvoiceResponseDto>(vCreate2).SubiektId);
    }

    [Fact]
    public async Task W1_PatchSameKeyOnOtherReceipt_ExecutesOnThatReceipt()
    {
        // Ta sama klasa na poziomie zasobu: klucz z PATCH sub_A użyty na PATCH sub_B dawał 200 z DTO dokumentu A, a B zostawał nietknięty.
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = new ReceiptsController(fake, store, NullLogger<ReceiptsController>.Instance);
        long a = (await fake.CreateReceiptAsync(Receipt(), CancellationToken.None)).SubiektId;
        long b = (await fake.CreateReceiptAsync(Receipt(), CancellationToken.None)).SubiektId;

        var (sa, _) = Unwrap((await c.PatchHeader($"sub_{a}", Patch("FZ 1/01/2026"), "k-ab", CancellationToken.None)).Result);
        Assert.Equal(200, sa);
        var (sb, vb) = Unwrap((await c.PatchHeader($"sub_{b}", Patch("FZ 2/01/2026"), "k-ab", CancellationToken.None)).Result);
        Assert.Equal(200, sb);
        var dto = Assert.IsType<ReceiptDetailDto>(vb);
        Assert.Equal(b, dto.SubiektId);
        Assert.Equal("FZ 2/01/2026", dto.OriginalNumber);
        Assert.Equal("FZ 2/01/2026", FakeSferaSession.OriginalNumberForTests(b));
        Assert.Equal("FZ 1/01/2026", FakeSferaSession.OriginalNumberForTests(a));
    }

    [Fact]
    public async Task W1_SameKeyOnOtherPostEndpoint_CreatesOwnDocument_NotReplayOfForeignResponse()
    {
        // Istniejące endpointy: klucz FS użyty w POST /receipts dawał replay FS jako „PZ" (200 z numerem FS) - PZ nie powstawało.
        var fake = new FakeSferaSession();
        var store = NewStore();
        var invoices = new InvoicesController(fake, store, NullLogger<InvoicesController>.Instance);
        var receipts = new ReceiptsController(fake, store, NullLogger<ReceiptsController>.Instance);

        var (sFs, vFs) = Unwrap((await invoices.Create(Invoice(), "k-shared", CancellationToken.None)).Result);
        Assert.Equal(201, sFs);
        var fs = Assert.IsType<InvoiceResponseDto>(vFs);

        var (sPz, vPz) = Unwrap((await receipts.Create(Receipt(), "k-shared", CancellationToken.None)).Result);
        Assert.Equal(201, sPz);
        var pz = Assert.IsType<InvoiceResponseDto>(vPz);
        Assert.NotEqual(fs.SubiektId, pz.SubiektId);
        Assert.StartsWith("PZ", pz.Number);
    }

    [Fact]
    public async Task W1_CorrectionSameKeyOnOtherInvoice_CreatesOwnCorrection()
    {
        // /corrections: KFS faktury 1 zwracana jako „korekta faktury 2" (200 z cache) - korekta faktury 2 nie powstawała.
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = new InvoicesController(fake, store, NullLogger<InvoicesController>.Instance);

        var (s1, v1) = Unwrap((await c.CreateCorrection("fake_inv_000001", Correction(), "k-kfs", CancellationToken.None)).Result);
        Assert.Equal(201, s1);
        var (s2, v2) = Unwrap((await c.CreateCorrection("fake_inv_000002", Correction(), "k-kfs", CancellationToken.None)).Result);
        Assert.Equal(201, s2);
        Assert.NotEqual(Assert.IsType<InvoiceResponseDto>(v1).SubiektId, Assert.IsType<InvoiceResponseDto>(v2).SubiektId);

        // Ponowienie na tym samym dokumencie nadal = replay (zgodny klient bez zmian).
        var (s3, v3) = Unwrap((await c.CreateCorrection("fake_inv_000002", Correction(), "k-kfs", CancellationToken.None)).Result);
        Assert.Equal(200, s3);
        Assert.Equal(Assert.IsType<InvoiceResponseDto>(v2).SubiektId, Assert.IsType<InvoiceResponseDto>(v3).SubiektId);
    }

    // ----------------------------- W1: IdempotencyStore -----------------------------

    [Fact]
    public async Task Store_SameKeyInDifferentScopes_SeparateEntries()
    {
        var store = NewStore();
        await store.SaveAsync(IdempotencyScope.CreateReceipt, "k", Doc(1), CancellationToken.None);
        Assert.Null(await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.UpdateReceiptHeader(1), "k", CancellationToken.None));
        Assert.Null(await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.CreateInvoice, "k", CancellationToken.None));

        await store.SaveAsync(IdempotencyScope.UpdateReceiptHeader(1), "k", Doc(2), CancellationToken.None);
        Assert.Null(await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.UpdateReceiptHeader(3), "k", CancellationToken.None));
        Assert.Equal(1, (await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.CreateReceipt, "k", CancellationToken.None))!.SubiektId);
        Assert.Equal(2, (await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.UpdateReceiptHeader(1), "k", CancellationToken.None))!.SubiektId);

        await store.DeleteAsync(IdempotencyScope.UpdateReceiptHeader(1), "k", CancellationToken.None);
        Assert.Null(await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.UpdateReceiptHeader(1), "k", CancellationToken.None));
        Assert.Equal(1, (await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.CreateReceipt, "k", CancellationToken.None))!.SubiektId);
    }

    [Fact]
    public async Task Store_UnscopedEntryFromBeforeUpgrade_ReadOnlyByPreExistingPosts()
    {
        // Wpis sprzed v0.21.0 (TTL 14 dni): 6 POST-ów czyta go jak dotąd (ponowienie przez aktualizację = replay), PATCH - nigdy.
        var (store, path) = NewStoreWithPath();
        InsertUnscoped(path, "k-old", Doc(7));
        foreach (var (scope, preExisting) in AllOperations)
        {
            var cached = await store.TryGetAsync<InvoiceResponseDto>(scope, "k-old", CancellationToken.None);
            if (preExisting) Assert.Equal(7, Assert.IsType<InvoiceResponseDto>(cached).SubiektId);
            else Assert.Null(cached);
        }

        await store.DeleteAsync(IdempotencyScope.UpdateReceiptHeader(1), "k-old", CancellationToken.None);
        Assert.True(RowExists(path, "k-old"));
        await store.DeleteAsync(IdempotencyScope.CreateReceipt, "k-old", CancellationToken.None);
        Assert.False(RowExists(path, "k-old"));
    }

    [Fact]
    public async Task Store_ScopedEntryWinsOverUnscoped()
    {
        var (store, path) = NewStoreWithPath();
        InsertUnscoped(path, "k", Doc(1));
        await store.SaveAsync(IdempotencyScope.CreateReceipt, "k", Doc(2), CancellationToken.None);
        Assert.Equal(2, (await store.TryGetAsync<InvoiceResponseDto>(IdempotencyScope.CreateReceipt, "k", CancellationToken.None))!.SubiektId);
        Assert.True(RowExists(path, IdempotencyScope.CreateReceipt.StorageKey("k")));
    }

    [Fact]
    public async Task W1_UnscopedEntry_PostReplays_PatchSameKeyExecutes()
    {
        // Warunek lidera: wpis w starym formacie → POST /receipts = replay jak przed aktualizacją, PATCH tym kluczem go NIE czyta (zapis numeru).
        var fake = new FakeSferaSession();
        var (store, path) = NewStoreWithPath();
        var c = new ReceiptsController(fake, store, NullLogger<ReceiptsController>.Instance);
        var created = await fake.CreateReceiptAsync(Receipt(), CancellationToken.None);
        InsertUnscoped(path, "test:order:1:PZ", created);

        var (sPost, vPost) = Unwrap((await c.Create(Receipt(), "test:order:1:PZ", CancellationToken.None)).Result);
        Assert.Equal(200, sPost);
        Assert.Equal(created.SubiektId, Assert.IsType<InvoiceResponseDto>(vPost).SubiektId);

        var (sPatch, vPatch) = Unwrap((await c.PatchHeader($"sub_{created.SubiektId}", Patch("FZ 1/01/2026"), "test:order:1:PZ", CancellationToken.None)).Result);
        Assert.Equal(200, sPatch);
        Assert.Equal("FZ 1/01/2026", Assert.IsType<ReceiptDetailDto>(vPatch).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(created.SubiektId));
        Assert.True(RowExists(path, "test:order:1:PZ"));
    }

    // ----------------------------- W1: census operacji -----------------------------

    [Fact]
    public void Scopes_AllOperations_DistinctPerEndpointAndDocument_OnlyPatchSkipsUnscoped()
    {
        var operations = AllOperations.Select(o => o.Scope.Operation).ToList();
        Assert.Equal(operations.Count, operations.Distinct(StringComparer.Ordinal).Count());
        Assert.All(operations, op => Assert.DoesNotContain("\n", op));
        Assert.NotEqual(IdempotencyScope.CreateCorrection(1).Operation, IdempotencyScope.CreateCorrection(2).Operation);
        Assert.NotEqual(IdempotencyScope.CreateSettlement(1).Operation, IdempotencyScope.CreateSettlement(2).Operation);
        Assert.NotEqual(IdempotencyScope.BookBankTransaction(1).Operation, IdempotencyScope.BookBankTransaction(2).Operation);
        Assert.NotEqual(IdempotencyScope.UpdateReceiptHeader(1).Operation, IdempotencyScope.UpdateReceiptHeader(2).Operation);
        Assert.All(AllOperations, o => Assert.Equal(o.PreExisting, o.Scope.ReadsUnscopedEntries));

        // Każda fabryka IdempotencyScope jest w AllOperations - nowa operacja wymaga decyzji o wpisach sprzed v0.21.0.
        var t = typeof(IdempotencyScope);
        int factories = t.GetFields(BindingFlags.Public | BindingFlags.Static).Count(f => f.FieldType == t)
                        + t.GetMethods(BindingFlags.Public | BindingFlags.Static).Count(m => m.ReturnType == t);
        Assert.Equal(AllOperations.Length, factories);
    }

    [Fact]
    public void Census_EveryActionWithIdempotencyKey_IsKnown()
    {
        // Akcje kontrolerów z nagłówkiem Idempotency-Key = operacje z AllOperations (nowy endpoint → fabryka w IdempotencyScope + wpis tu).
        var actions = typeof(ReceiptsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Any(p => p.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key"))
                .Select(m => $"{t.Name}.{m.Name}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            "BankTransactionsController.Book",
            "InvoicesController.Create",
            "InvoicesController.CreateCorrection",
            "ReceiptsController.Create",
            "ReceiptsController.PatchHeader",
            "SettlementsController.Create",
            "TransfersController.Create",
        }, actions);
        Assert.Equal(AllOperations.Length, actions.Length);
    }
}
