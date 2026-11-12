using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Tests;

public sealed class JobApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // Queued jobs are created far in the future, so the server's own worker
    // leaves them alone while the test looks at them.
    private async Task<Job> AddJobAsync(Guid? owner, JobState state = JobState.Queued, string? errorDetail = null)
    {
        var now = _factory.Time.GetUtcNow();
        var job = new Job
        {
            Type = "test.job",
            State = state,
            OwnerUserId = owner,
            RunAfter = now.AddYears(1),
            CreatedAt = now,
            ErrorCode = errorDetail is null ? null : "job.unexpected_error",
            ErrorDetail = errorDetail,
            FinishedAt = state is JobState.Queued or JobState.Running ? null : now,
        };
        await _factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        });
        return job;
    }

    [Fact]
    public async Task Signing_in_is_required()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_sees_only_their_own_jobs()
    {
        var ana = await _factory.CreateUserAsync("ana", TestApi.Password);
        var bob = await _factory.CreateUserAsync("bob", TestApi.Password);
        var mine = await AddJobAsync(ana.Id);
        var theirs = await AddJobAsync(bob.Id);
        await AddJobAsync(owner: null);
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("ana")).AccessToken;

        var page = await (await client.SendAsync(HttpMethod.Get, "/api/v1/jobs", token)).ReadAsync<JobPage>();
        var other = await client.SendAsync(HttpMethod.Get, $"/api/v1/jobs/{theirs.Id}", token);

        var item = Assert.Single(page.Items);
        Assert.Equal(mine.Id, item.Id);
        Assert.True(item.Mine);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task An_admin_sees_all_jobs_but_not_the_details_of_another_users_failure()
    {
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        var bob = await _factory.CreateUserAsync("bob", TestApi.Password);
        var users = await AddJobAsync(bob.Id, JobState.Failed, errorDetail: "IOException: /photos/holiday.jpg");
        var servers = await AddJobAsync(null, JobState.Failed, errorDetail: "IOException: disk full");
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("root")).AccessToken;

        // Filtered by type: the server also schedules its own housekeeping jobs.
        var page = await (await client.SendAsync(HttpMethod.Get, "/api/v1/jobs?type=test.job", token)).ReadAsync<JobPage>();

        Assert.Equal(2, page.Items.Count);
        var user = page.Items.Single(j => j.Id == users.Id);
        Assert.Equal("job.unexpected_error", user.ErrorCode);
        Assert.Null(user.ErrorDetail);
        Assert.False(user.Mine);
        Assert.Equal("IOException: disk full", page.Items.Single(j => j.Id == servers.Id).ErrorDetail);
    }

    [Fact]
    public async Task Jobs_are_listed_newest_first_in_pages()
    {
        var ana = await _factory.CreateUserAsync("ana", TestApi.Password);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await AddJobAsync(ana.Id)).Id);

            // UUIDv7 orders by millisecond; within one millisecond the order is random.
            await Task.Delay(3);
        }

        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("ana")).AccessToken;

        var first = await (await client.SendAsync(HttpMethod.Get, "/api/v1/jobs?limit=3", token)).ReadAsync<JobPage>();
        var second = await (await client.SendAsync(HttpMethod.Get, $"/api/v1/jobs?limit=3&cursor={first.NextCursor}", token)).ReadAsync<JobPage>();

        ids.Reverse();
        Assert.Equal(ids, first.Items.Concat(second.Items).Select(j => j.Id));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Jobs_are_filtered_by_state_as_the_contract_spells_it()
    {
        var ana = await _factory.CreateUserAsync("ana", TestApi.Password);
        var failed = await AddJobAsync(ana.Id, JobState.Failed, errorDetail: "x");
        await AddJobAsync(ana.Id);
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("ana")).AccessToken;

        var page = await (await client.SendAsync(HttpMethod.Get, "/api/v1/jobs?state=failed", token)).ReadAsync<JobPage>();
        var unknown = await client.SendAsync(HttpMethod.Get, "/api/v1/jobs?state=exploded", token);

        Assert.Equal(failed.Id, Assert.Single(page.Items).Id);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task A_bad_limit_or_cursor_is_refused()
    {
        await _factory.CreateUserAsync("ana", TestApi.Password);
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("ana")).AccessToken;

        var tooMany = await client.SendAsync(HttpMethod.Get, "/api/v1/jobs?limit=1000", token);
        var badCursor = await client.SendAsync(HttpMethod.Get, "/api/v1/jobs?cursor=nope", token);

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
    }

    [Fact]
    public async Task A_user_cancels_their_queued_job_once()
    {
        var ana = await _factory.CreateUserAsync("ana", TestApi.Password);
        var job = await AddJobAsync(ana.Id);
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("ana")).AccessToken;

        var cancel = await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{job.Id}/cancel", token);
        var again = await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{job.Id}/cancel", token);

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("job.not_active", await again.ProblemCodeAsync());
        await _factory.WithDbAsync(async db =>
            Assert.Equal(JobState.Cancelled, (await db.Jobs.SingleAsync(j => j.Id == job.Id)).State));
    }

    [Fact]
    public async Task A_user_cannot_cancel_someone_elses_job()
    {
        await _factory.CreateUserAsync("ana", TestApi.Password);
        var bob = await _factory.CreateUserAsync("bob", TestApi.Password);
        var job = await AddJobAsync(bob.Id);
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("ana")).AccessToken;

        var cancel = await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{job.Id}/cancel", token);

        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        await _factory.WithDbAsync(async db =>
            Assert.Equal(JobState.Queued, (await db.Jobs.SingleAsync(j => j.Id == job.Id)).State));
    }

    [Fact]
    public async Task Only_a_failed_or_cancelled_job_can_be_retried()
    {
        var ana = await _factory.CreateUserAsync("ana", TestApi.Password);
        var failed = await AddJobAsync(ana.Id, JobState.Failed, errorDetail: "boom");
        var succeeded = await AddJobAsync(ana.Id, JobState.Succeeded);
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync("ana")).AccessToken;

        var retry = await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{failed.Id}/retry", token);
        var refused = await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{succeeded.Id}/retry", token);

        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("job.not_retryable", await refused.ProblemCodeAsync());
    }
}
