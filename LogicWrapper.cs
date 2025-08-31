using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using System.Linq;
using System.Collections.Concurrent;

static class LogicWrapper
{

    const string ResultsFilePath = "listings.json";
    static HashSet<string> listingsUrls = new();
    const int MinValueOfMeters = 55;
    const int MaxValueOfRent = 5000;

    public static async Task RunAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false }); // Headless for speed
        var page = await browser.NewPageAsync();

        // Open Otodom homepage
        await page.GotoAsync("https://www.otodom.pl/");

        await AcceptCookies(page);//accept cookies
        var res = page.GetByText("Na sprzedaż"); //get dropdown div to click on
        await res.First.ClickAsync(); //click it
        await page.ClickAsync("div[id='react-select-transaction-option-0']"); //cjoose to rent

        await page.FillAsync("input[data-cy='search.form.location.button']", "Krakow");
        await page.ClickAsync("div[data-sentry-element='StyledListItem']");

        //district selection
        await page.ClickAsync("div[data-listkey='malopolskie/krakow/krakow/krakow/czyzyny']");
        await page.ClickAsync("div[data-listkey='malopolskie/krakow/krakow/krakow/grzegorzki']");
        await page.ClickAsync("div[data-listkey='malopolskie/krakow/krakow/krakow/krowodrza']");
        await page.ClickAsync("div[data-listkey='malopolskie/krakow/krakow/krakow/podgorze']");
        await page.ClickAsync("div[data-listkey='malopolskie/krakow/krakow/krakow/pradnik-bialy']");
        await page.ClickAsync("div[data-listkey='malopolskie/krakow/krakow/krakow/pradnik-czerwony']");
        await page.ClickAsync("div[data-listkey='malopolskie/krakow/krakow/krakow/stare-miasto']");

        //price input
        await page.FillAsync("input[id='priceMax']", "4000");

        //square meters minimum input
        await page.FillAsync("input[id='areaMin']", "55");

        //click search button
        await page.ClickAsync("button[id='search-form-submit']");

        await page.ClickAsync("button[data-cy='search.form.more-filters']");
        await page.ClickAsync("label[data-cy='search-form--field--extras--LIFT--label']");
        await page.ClickAsync("label[data-cy='search-form--field--extras--GARAGE--label']");
        await page.ClickAsync("input[name='daysSinceCreated']");
        await page.ClickAsync("label[data-cy='search-form--field--extras--AIR_CONDITIONING--label']");

        await page.ClickAsync("button[id='search-form-submit']");


        await page.WaitForSelectorAsync("div[data-sentry-element='Content']");
        var r = await page.Locator("section[data-sentry-element='StyledWrapper']").CountAsync();
        string selector = "ul.css-1rdakgn > li:has(article)";
        var listingsArr = await page.Locator(selector).AllAsync();

        await RunListingProcessingInBatchAsync(browser, listingsArr, 5, 5);

        JsonSerializerOptions options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        if (File.Exists(ResultsFilePath))
        {
            var existingData = await File.ReadAllTextAsync(ResultsFilePath);
            HashSet<string> existingUrls = System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(existingData)!;

            if (listingsUrls.Any())
            {
                existingUrls.UnionWith(existingUrls);
                var json = System.Text.Json.JsonSerializer.Serialize(existingUrls, options);
                await File.WriteAllTextAsync("listings.json", json);
            }
        }
        else
        {
            if (listingsUrls.Any())
            {
                var json = System.Text.Json.JsonSerializer.Serialize(listingsUrls, options);
                await File.WriteAllTextAsync("listings.json", json);
            }
        }



        await browser.CloseAsync();
    }

    static async Task AcceptCookies(IPage page)
    {
        try
        {
            await page.ClickAsync("button[id='onetrust-accept-btn-handler']"); //accept cookies
        }
        catch (PlaywrightException)
        {
            // Cookie banner not found; proceed without action
        }
    }

    static bool TotalSumIsOk(string input, int maxTotalSum = 5000)
    {
        HashSet<int> numbers = new();

        var pattern = @"^(?!.*:)[\d\s,]+$";
        var match = Regex.Matches(input, @"\d{3,}|\d{1}(\s|\S)\d{2,}");
        var matchWithWords = Regex.Matches(input, @"[ ]*\d{3,}[ ]*|[ ]*\d{1}(\s[ ]*|[ ]*\S)\d{2,[ ]*}");
        for (int i = 0; i <= match.Count - 1; i++)
        {
            var value = match[i].Value;

            if (Regex.IsMatch(value, pattern))
            {
                var currMatch = int.Parse(
                    Regex.Replace(value, @"\s", "").Replace(",", "")
                );

                numbers.Add(currMatch);
            }
        }
        var sortedNumbers = (numbers as IEnumerable<int>).OrderDescending().ToArray();
        int presumableKaucja = sortedNumbers[0]; //assume that the biggest number is the kaucja for apartment

        var totalSum = sortedNumbers.Sum() - presumableKaucja;
        //Console.WriteLine(totalSum);

        return totalSum <= maxTotalSum;
    }

    async static Task RunListingProcessingInBatchAsync(IBrowser browser, IReadOnlyList<ILocator> listings, int batchSize = 5, int maxConcurrency = 5)
    {
        // Validate inputs
        if (listings == null || !listings.Any())
        {
            Console.WriteLine("No listings to process.");
            return;
        }

        // Create a SemaphoreSlim to limit concurrency
        // using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var opts = new ParallelOptions() { MaxDegreeOfParallelism = maxConcurrency };

        await Parallel.ForEachAsync(listings.Batch(batchSize), opts,
                async (batch, ct) =>
                {
                    await Task.WhenAll(batch.Select(async listing =>
                    {
                        await ProcessListingAsync(listing, browser);
                    }));
                });

        /*
        // Process tasks in batches
        // foreach (var batch in operations.Chunk(batchSize))
        // {
        //     Console.WriteLine($"Batch start Size -> {batch.Length}");

        //     // Create a list to hold tasks for this batch
        //     var tasks = new List<Task>();

        //     foreach (var operation in batch)
        //     {
        //         // Wait for an available slot in the semaphore
        //         await semaphore.WaitAsync();

        //         // Start the task and handle its completion
        //         tasks.Add(Task.Run(async () =>
        //         {
        //             try
        //             {
        //                 await operation();
        //             }
        //             catch (Exception ex)
        //             {
        //                 Console.WriteLine($"Error processing listing: {ex.Message}");
        //                 // Optionally log the error or handle it as needed
        //             }
        //             finally
        //             {
        //                 semaphore.Release();
        //             }
        //         }));
        //     }

        //     // Wait for all tasks in the current batch to complete
        //     await Task.WhenAll(tasks);
        // }
        */

        Console.WriteLine("All batches processed.");
    }

    async static Task RunListingProcessingInBatchAsync(IBrowser browser, IReadOnlyList<ILocator> listings, int batchSize = 5)
    {
        var factory = new TaskFactory();

        var operations = listings.Select(listing => factory.StartNew(() => ProcessListingAsync(listing, browser))).ToList();
        var listOfChuncks = operations.Chunk(batchSize).ToList();
        foreach (var batch in operations.Chunk(batchSize))
        {
            Console.WriteLine("Batch start Size -> {0}", batch.Length);

            // Start all the tasks in this batch
            var tasks = batch.ToList();

            // Wait for them to complete
            await Task.WhenAll(tasks);
        }
    }

    async static Task ProcessListingAsync(ILocator listingLocator, IBrowser browser)
    {
        var listingData = (await listingLocator.Locator("a[data-sentry-element='Link']").AllAsync()).FirstOrDefault();

        if (listingData == null) return;

        var listingHref = await listingData.GetAttributeAsync("href");

        Console.WriteLine("Processing listing: {0}", listingHref);

        var listingLinkToClick = "https://www.otodom.pl" + listingHref;

        string regexPattern = new Regex(@"\d{1,}").ToString();

        // Open each listing in a new tab
        var listingPage = await browser.NewPageAsync();
        var response = await listingPage.GotoAsync(listingLinkToClick);

        await AcceptCookies(listingPage);//accept cookies

        var rentValuePerMonth = await listingPage.Locator("div[data-sentry-element='MainPriceWrapper']").InnerTextAsync();
        var safeRentValuePerMonth = !string.IsNullOrWhiteSpace(rentValuePerMonth) ? Regex.Match(rentValuePerMonth.Replace(" ", ""), regexPattern).Value : string.Empty;

        var divs = (await listingPage.Locator("div[data-sentry-element='ItemGridContainer']").AllAsync()).ToList(); // was

        var divWithSquareMeters = await divs.ElementAt(0).InnerTextAsync();
        var divWithKaucja = await divs.ElementAt(7).InnerTextAsync();
        var divWithCzynsz = await divs.ElementAt(6).InnerTextAsync();

        var metersStr = Regex.Match(divWithSquareMeters.Replace(" ", ""), regexPattern).Value;
        var kaucjaStr = Regex.Match(divWithKaucja.Replace(" ", ""), regexPattern).Value;
        var czynszStr = Regex.Match(divWithCzynsz.Replace(" ", ""), regexPattern).Value;

        var meters = string.IsNullOrWhiteSpace(metersStr) ? 0 : int.Parse(metersStr);
        var kaucja = string.IsNullOrWhiteSpace(kaucjaStr) ? 0 : int.Parse(kaucjaStr);
        var czynsz = string.IsNullOrWhiteSpace(czynszStr) ? 0 : int.Parse(czynszStr);

        var description = await listingPage.Locator("div[data-sentry-component='AdDescriptionBase']").InnerTextAsync();

        if (int.TryParse(safeRentValuePerMonth, out int valuePerMonth))
        {
            if (valuePerMonth + czynsz <= MaxValueOfRent && TotalSumIsOk(description) && meters >= MinValueOfMeters)
            {
                listingsUrls.Add(listingLinkToClick);
            }
        }

        await listingPage.CloseAsync();
    }
}

public static class BatchExtensions
{
    public static IEnumerable<List<T>> Batch<T>(this IEnumerable<T> col, int batchSize = 2000)
    {
        var batch = new List<T>(batchSize);
        foreach (var o in col)
        {
            batch.Add(o);
            if (batch.Count == batchSize)
            {
                var rc = batch;
                batch = new List<T>(batchSize);
                yield return rc;
            }
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}