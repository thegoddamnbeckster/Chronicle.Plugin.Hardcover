using System.Text.Json.Serialization;

namespace Chronicle.Plugin.Hardcover;

// ── GraphQL request ───────────────────────────────────────────────────────────

internal record GraphQlRequest(
    [property: JsonPropertyName("query")]     string Query,
    [property: JsonPropertyName("variables")] object? Variables = null
);

// ── GraphQL response envelope ─────────────────────────────────────────────────

internal class GraphQlResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public GraphQlError[]? Errors { get; set; }
}

internal class GraphQlError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

// ── /me — identity ping ───────────────────────────────────────────────────────

internal class MeData
{
    [JsonPropertyName("me")]
    public MeUser[]? Me { get; set; }
}

internal class MeUser
{
    [JsonPropertyName("id")]       public int    Id       { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
}

// ── user_books — shared base ──────────────────────────────────────────────────

internal class UserBooksData
{
    [JsonPropertyName("user_books")]
    public UserBook[]? UserBooks { get; set; }
}

internal class UserBook
{
    [JsonPropertyName("id")]              public int        Id          { get; set; }
    [JsonPropertyName("book")]            public HcBook?    Book        { get; set; }
    [JsonPropertyName("rating")]          public double?    Rating      { get; set; }
    [JsonPropertyName("inserted_at")]     public string?    InsertedAt  { get; set; }
    [JsonPropertyName("user_book_reads")] public HcRead[]?  Reads       { get; set; }
}

internal class HcBook
{
    [JsonPropertyName("id")]           public int     Id          { get; set; }
    [JsonPropertyName("title")]        public string  Title       { get; set; } = string.Empty;
    [JsonPropertyName("release_year")] public int?    ReleaseYear { get; set; }

    [JsonPropertyName("book_mappings")]
    public HcBookMapping[]? BookMappings { get; set; }
}

internal class HcBookMapping
{
    [JsonPropertyName("isbn_13")] public string? Isbn13 { get; set; }
    [JsonPropertyName("isbn_10")] public string? Isbn10 { get; set; }
}

internal class HcRead
{
    [JsonPropertyName("finished_at")] public string? FinishedAt { get; set; }
    [JsonPropertyName("started_at")]  public string? StartedAt  { get; set; }
}
