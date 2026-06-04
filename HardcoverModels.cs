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
    [JsonPropertyName("updated_at")]      public string?    UpdatedAt   { get; set; }
    [JsonPropertyName("date_added")]      public string?    DateAdded   { get; set; }
    [JsonPropertyName("user_book_reads")] public HcRead[]?  Reads       { get; set; }
}

internal class HcBook
{
    [JsonPropertyName("id")]           public int     Id          { get; set; }
    [JsonPropertyName("title")]        public string  Title       { get; set; } = string.Empty;
    [JsonPropertyName("release_year")] public int?    ReleaseYear { get; set; }

    [JsonPropertyName("book_mappings")]
    public HcBookMapping[]? BookMappings { get; set; }

    [JsonPropertyName("contributions")]
    public HcContribution[]? Contributions { get; set; }

    [JsonPropertyName("book_series")]
    public HcBookSeries[]? BookSeries { get; set; }
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

// ── Search result shapes (results field is jsonb — use JsonElement) ──────────

internal class SearchData
{
    [JsonPropertyName("search")]
    public SearchOutput? Search { get; set; }
}

internal class SearchOutput
{
    [JsonPropertyName("results")]
    public System.Text.Json.JsonElement Results { get; set; }
}

// ── Author ────────────────────────────────────────────────────────────────────

internal class AuthorData
{
    [JsonPropertyName("authors")]
    public HcAuthor[]? Authors { get; set; }
}

internal class HcAuthor
{
    [JsonPropertyName("id")]              public int      Id             { get; set; }
    [JsonPropertyName("name")]            public string   Name           { get; set; } = string.Empty;
    [JsonPropertyName("bio")]             public string?  Bio            { get; set; }
    [JsonPropertyName("slug")]            public string?  Slug           { get; set; }
    [JsonPropertyName("image")]           public HcImage? Image          { get; set; }
    /// <summary>Pen names and name variants (e.g. ["K. J. Parker"] for Tom Holt).</summary>
    [JsonPropertyName("alternate_names")] public string[]? AlternateNames { get; set; }
}

// ── Series ────────────────────────────────────────────────────────────────────

internal class SeriesData
{
    [JsonPropertyName("series")]
    public HcSeries[]? Series { get; set; }
}

internal class HcSeries
{
    [JsonPropertyName("id")]           public int     Id           { get; set; }
    [JsonPropertyName("name")]         public string  Name         { get; set; } = string.Empty;
    [JsonPropertyName("description")]  public string? Description  { get; set; }
    [JsonPropertyName("slug")]         public string? Slug         { get; set; }
    [JsonPropertyName("is_completed")] public bool?   IsCompleted  { get; set; }
    [JsonPropertyName("book_series")]  public HcBookSeriesEntry[]? BookSeries { get; set; }
}

internal class HcBookSeriesEntry
{
    [JsonPropertyName("position")] public double?     Position { get; set; }
    [JsonPropertyName("book")]     public HcBookStub? Book     { get; set; }
}

internal class HcBookStub
{
    [JsonPropertyName("id")]            public int              Id            { get; set; }
    [JsonPropertyName("title")]         public string           Title         { get; set; } = string.Empty;
    [JsonPropertyName("image")]         public HcImage?         Image         { get; set; }
    /// <summary>Author contributions — populated when the query requests them.</summary>
    [JsonPropertyName("contributions")] public HcContribution[]? Contributions { get; set; }
}

// ── Book detail ───────────────────────────────────────────────────────────────

internal class BookDetailData
{
    [JsonPropertyName("books")]
    public HcBookDetail[]? Books { get; set; }
}

internal class HcBookDetail
{
    [JsonPropertyName("id")]               public int               Id            { get; set; }
    [JsonPropertyName("title")]            public string            Title         { get; set; } = string.Empty;
    [JsonPropertyName("subtitle")]         public string?           Subtitle      { get; set; }
    [JsonPropertyName("description")]      public string?           Description   { get; set; }
    [JsonPropertyName("release_year")]     public int?              ReleaseYear   { get; set; }
    [JsonPropertyName("pages")]            public int?              Pages         { get; set; }
    [JsonPropertyName("rating")]           public double?           Rating        { get; set; }
    [JsonPropertyName("ratings_count")]    public int?              RatingsCount  { get; set; }
    [JsonPropertyName("cached_tags")]      public System.Text.Json.JsonElement? CachedTags { get; set; }
    [JsonPropertyName("image")]            public HcImage?          Image         { get; set; }
    [JsonPropertyName("contributions")]    public HcContribution[]? Contributions { get; set; }
    [JsonPropertyName("book_series")]      public HcBookSeries[]?   BookSeries    { get; set; }
    [JsonPropertyName("book_mappings")]    public HcBookMapping[]?  BookMappings  { get; set; }
    [JsonPropertyName("default_physical_edition")] public HcEdition? DefaultEdition { get; set; }
}

internal class HcImage
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

internal class HcContribution
{
    [JsonPropertyName("author")]       public HcAuthorStub? Author       { get; set; }
    [JsonPropertyName("contribution")] public string?       Contribution { get; set; }
}

internal class HcAuthorStub
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

internal class HcBookSeries
{
    [JsonPropertyName("position")] public double?       Position { get; set; }
    [JsonPropertyName("series")]   public HcSeriesStub? Series   { get; set; }
}

internal class HcSeriesStub
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

internal class HcEdition
{
    [JsonPropertyName("audio_seconds")]  public int?           AudioSeconds { get; set; }
    [JsonPropertyName("narrations")]     public HcNarration[]? Narrations   { get; set; }
}

internal class HcNarration
{
    [JsonPropertyName("narrator")] public HcNarratorStub? Narrator { get; set; }
}

internal class HcNarratorStub
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

// ── Slug resolution ───────────────────────────────────────────────────────────

internal class SlugLookupData<T>
{
    [JsonPropertyName("books")]   public T[]? Books   { get; set; }
    [JsonPropertyName("series")]  public T[]? Series  { get; set; }
    [JsonPropertyName("authors")] public T[]? Authors { get; set; }
}

internal class HcIdOnly
{
    [JsonPropertyName("id")] public int Id { get; set; }
}
