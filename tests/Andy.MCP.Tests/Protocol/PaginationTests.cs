using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class PaginationHelperTests
{
    private const string SessionKey = "test-session-key-12345";

    [Fact]
    public void FirstPage_NoCursor_ReturnsItemsAndNextCursor()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 3);
        var items = Enumerable.Range(1, 10).ToList();

        var result = helper.GetPage(items, cursor: null);

        Assert.Equal([1, 2, 3], result.Items);
        Assert.NotNull(result.NextCursor);
        Assert.True(result.HasMore);
    }

    [Fact]
    public void SecondPage_WithCursor_ReturnsNextItems()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 3);
        var items = Enumerable.Range(1, 10).ToList();

        var page1 = helper.GetPage(items, cursor: null);
        var page2 = helper.GetPage(items, cursor: page1.NextCursor);

        Assert.Equal([4, 5, 6], page2.Items);
        Assert.NotNull(page2.NextCursor);
    }

    [Fact]
    public void LastPage_HasNoNextCursor()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 3);
        var items = Enumerable.Range(1, 5).ToList();

        var page1 = helper.GetPage(items, cursor: null);       // [1,2,3]
        var page2 = helper.GetPage(items, cursor: page1.NextCursor); // [4,5]

        Assert.Equal([4, 5], page2.Items);
        Assert.Null(page2.NextCursor);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public void ExactPageSize_LastPageHasNoCursor()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 3);
        var items = Enumerable.Range(1, 6).ToList();

        var page1 = helper.GetPage(items, cursor: null);        // [1,2,3]
        var page2 = helper.GetPage(items, cursor: page1.NextCursor); // [4,5,6]

        Assert.Equal([4, 5, 6], page2.Items);
        Assert.Null(page2.NextCursor);
    }

    [Fact]
    public void EmptyCollection_ReturnsEmptyAndNoCursor()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 10);

        var result = helper.GetPage<int>([], cursor: null);

        Assert.Empty(result.Items);
        Assert.Null(result.NextCursor);
        Assert.False(result.HasMore);
    }

    [Fact]
    public void SingleItemCollection_FitsInOnePage()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 10);

        var result = helper.GetPage([42], cursor: null);

        Assert.Equal([42], result.Items);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public void CustomPageSize_Override()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 100);
        var items = Enumerable.Range(1, 10).ToList();

        var result = helper.GetPage(items, cursor: null, pageSize: 2);

        Assert.Equal([1, 2], result.Items);
        Assert.NotNull(result.NextCursor);
    }

    [Fact]
    public void PageSize_One_PaginatesOneByOne()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 1);
        var items = new List<string> { "a", "b", "c" };

        var page1 = helper.GetPage(items, cursor: null);
        Assert.Equal(["a"], page1.Items);

        var page2 = helper.GetPage(items, cursor: page1.NextCursor);
        Assert.Equal(["b"], page2.Items);

        var page3 = helper.GetPage(items, cursor: page2.NextCursor);
        Assert.Equal(["c"], page3.Items);
        Assert.Null(page3.NextCursor);
    }

    [Fact]
    public void LargeCollection_PaginatesCorrectly()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 100);
        var items = Enumerable.Range(1, 1050).ToList();
        var allCollected = new List<int>();
        string? cursor = null;
        int pageCount = 0;

        do
        {
            var page = helper.GetPage(items, cursor);
            allCollected.AddRange(page.Items);
            cursor = page.NextCursor;
            pageCount++;
        } while (cursor is not null);

        Assert.Equal(items, allCollected);
        Assert.Equal(11, pageCount); // 10 full pages + 1 partial (50 items)
    }

    [Fact]
    public void InvalidCursor_RandomString_Throws()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 10);

        var ex = Assert.Throws<McpPaginationException>(() =>
            helper.GetPage([1, 2, 3], cursor: "totally-invalid"));
        Assert.Contains("Invalid cursor", ex.Message);
    }

    [Fact]
    public void TamperedCursor_ModifiedPayload_Throws()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 3);
        var items = Enumerable.Range(1, 10).ToList();

        var page1 = helper.GetPage(items, cursor: null);
        var cursor = page1.NextCursor!;

        // Tamper with the payload part (before the dot)
        var parts = cursor.Split('.');
        var tampered = Convert.ToBase64String("tampered"u8.ToArray()) + "." + parts[1];

        var ex = Assert.Throws<McpPaginationException>(() =>
            helper.GetPage(items, cursor: tampered));
        Assert.Contains("signature mismatch", ex.Message);
    }

    [Fact]
    public void CrossSessionCursor_DifferentKey_Throws()
    {
        var helper1 = new PaginationHelper("session-A", defaultPageSize: 3);
        var helper2 = new PaginationHelper("session-B", defaultPageSize: 3);
        var items = Enumerable.Range(1, 10).ToList();

        var page1 = helper1.GetPage(items, cursor: null);

        var ex = Assert.Throws<McpPaginationException>(() =>
            helper2.GetPage(items, cursor: page1.NextCursor));
        Assert.Contains("signature mismatch", ex.Message);
    }

    [Fact]
    public void ConcurrentPagination_IndependentCursors()
    {
        var helper = new PaginationHelper(SessionKey, defaultPageSize: 2);
        var items = Enumerable.Range(1, 6).ToList();

        // Two independent pagination sequences
        var seqA_page1 = helper.GetPage(items, cursor: null);
        var seqB_page1 = helper.GetPage(items, cursor: null);

        var seqA_page2 = helper.GetPage(items, cursor: seqA_page1.NextCursor);
        var seqB_page2 = helper.GetPage(items, cursor: seqB_page1.NextCursor);

        // Both should get the same results independently
        Assert.Equal(seqA_page1.Items, seqB_page1.Items);
        Assert.Equal(seqA_page2.Items, seqB_page2.Items);
    }
}

public class PaginatedRequestResultTests
{
    [Fact]
    public void PaginatedRequest_NoCursor_SerializesWithoutField()
    {
        var request = new PaginatedRequest();
        var json = System.Text.Json.JsonSerializer.Serialize(request, McpJsonDefaults.Options);

        Assert.DoesNotContain("cursor", json);
    }

    [Fact]
    public void PaginatedRequest_WithCursor_Serializes()
    {
        var request = new PaginatedRequest { Cursor = "abc123" };
        var json = System.Text.Json.JsonSerializer.Serialize(request, McpJsonDefaults.Options);

        Assert.Contains("\"cursor\":\"abc123\"", json);
    }

    [Fact]
    public void PaginatedResult_NoNextCursor_SerializesWithoutField()
    {
        var result = new PaginatedResult();
        var json = System.Text.Json.JsonSerializer.Serialize(result, McpJsonDefaults.Options);

        Assert.DoesNotContain("nextCursor", json);
    }

    [Fact]
    public void PaginatedResult_WithNextCursor_Serializes()
    {
        var result = new PaginatedResult { NextCursor = "next-page" };
        var json = System.Text.Json.JsonSerializer.Serialize(result, McpJsonDefaults.Options);

        Assert.Contains("\"nextCursor\":\"next-page\"", json);
    }
}

public class PaginationExtensionsTests
{
    [Fact]
    public async Task PaginateAll_SinglePage()
    {
        var items = new List<string> { "a", "b", "c" };

        var result = new List<string>();
        await foreach (var item in PaginationExtensions.PaginateAllAsync<string>(
            (cursor, ct) => Task.FromResult<(IReadOnlyList<string>, string?)>((items, null))))
        {
            result.Add(item);
        }

        Assert.Equal(["a", "b", "c"], result);
    }

    [Fact]
    public async Task PaginateAll_MultiplePages()
    {
        var pages = new List<(IReadOnlyList<string> items, string? next)>
        {
            (["a", "b"], "cursor1"),
            (["c", "d"], "cursor2"),
            (["e"], null)
        };
        int pageIndex = 0;

        var result = new List<string>();
        await foreach (var item in PaginationExtensions.PaginateAllAsync<string>(
            (cursor, ct) =>
            {
                var page = pages[pageIndex++];
                return Task.FromResult(page);
            }))
        {
            result.Add(item);
        }

        Assert.Equal(["a", "b", "c", "d", "e"], result);
        Assert.Equal(3, pageIndex);
    }

    [Fact]
    public async Task PaginateAll_EmptyResult()
    {
        var result = new List<string>();
        await foreach (var item in PaginationExtensions.PaginateAllAsync<string>(
            (cursor, ct) => Task.FromResult<(IReadOnlyList<string>, string?)>(([], null))))
        {
            result.Add(item);
        }

        Assert.Empty(result);
    }

    [Fact]
    public async Task PaginateAll_CancellationRespected()
    {
        var cts = new CancellationTokenSource();
        int pagesRequested = 0;

        var result = new List<int>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in PaginationExtensions.PaginateAllAsync<int>(
                (cursor, ct) =>
                {
                    pagesRequested++;
                    if (pagesRequested == 2)
                        cts.Cancel();
                    return Task.FromResult<(IReadOnlyList<int>, string?)>(([pagesRequested], $"cursor-{pagesRequested}"));
                }, cts.Token))
            {
                result.Add(item);
            }
        });

        Assert.Equal([1, 2], result);
    }
}
