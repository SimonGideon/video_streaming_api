using Microsoft.EntityFrameworkCore;
using VideoStreamingApi.Models;

namespace VideoStreamingApi.Data;

public class VideoStreamingDbContext(DbContextOptions<VideoStreamingDbContext> options) : DbContext(options)
{
    public DbSet<Video> Videos => Set<Video>();
}
