﻿using SQLite;
using System;

namespace Administrator.Services.Database.Models
{
    [Table("Suggestions")]
    public class Suggestion : IDbModel
    {
        [PrimaryKey]
        [AutoIncrement]
        [Column("SuggestionId")]
        public long Id { get; set; }

        public long MessageId { get; set; }

        public long GuildId { get; set; }

        public long UserId { get; set; }

        public string Content { get; set; }

        public string ImageUrl { get; set; } = string.Empty;

        public long Upvotes { get; set; }

        public long Downvotes { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}