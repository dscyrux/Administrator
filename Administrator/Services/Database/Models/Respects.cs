﻿using System;
using System.Collections.Generic;
using System.Text;
using SQLite;

namespace Administrator.Services.Database.Models
{
    [Table("Respects")]
    public class Respects : IDbModel
    {
        [PrimaryKey]
        [AutoIncrement]
        [Column("InternalId")]
        public long Id { get; set; }

        public long UserId { get; set; }

        public long GuildId { get; set; }

        public DateTimeOffset Timestamp { get; set;  } = DateTimeOffset.UtcNow;
    }
}
