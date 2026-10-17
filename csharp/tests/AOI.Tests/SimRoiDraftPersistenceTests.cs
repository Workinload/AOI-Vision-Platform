using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class SimRoiDraftPersistenceTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-roi-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void MasterOwnedRoiIsDurableRevisionedAndNeverLeaksIntoSlave()
    {
        var path = Path.Combine(_root, "master", "sim-config.sqlite");
        var master = new RoiConfigStore(path, MachineRole.Master);
        var initial = new RoiDraftEdit("CH06", .1, .1, .7, .7, 0, "sim_operator");
        var saved = master.Save(initial);
        Assert.That(saved.Revision, Is.EqualTo(1));
        Assert.That(saved.X, Is.EqualTo(.1));
        Assert.That(master.Snapshot(), Has.Count.EqualTo(1));

        Assert.Throws<InvalidOperationException>(() => master.Save(initial));
        var updated = master.Save(initial with { X = .2, ExpectedRevision = 1 });
        Assert.That(updated.Revision, Is.EqualTo(2));
        Assert.That(updated.X, Is.EqualTo(.2));

        master = new RoiConfigStore(path, MachineRole.Master);
        var restored = master.Snapshot();
        Assert.That(restored, Has.Count.EqualTo(1));
        Assert.That(restored.Single().Revision, Is.EqualTo(2));
        Assert.That(restored.Single().X, Is.EqualTo(.2));
        Assert.Throws<InvalidOperationException>(() =>
            master.Save(new RoiDraftEdit("CH02", .1, .1, .7, .7, 0, "sim_operator")));
        var slave = new RoiConfigStore(Path.Combine(_root, "slave", "sim-config.sqlite"), MachineRole.Slave);
        Assert.That(slave.Snapshot(), Is.Empty);
        Assert.Throws<InvalidOperationException>(() => slave.Save(
            new RoiDraftEdit("CH06", .1, .1, .7, .7, 0, "sim_operator")));
        var slaveSaved = slave.Save(new RoiDraftEdit("CH02", 0, 0, 1, 1, 0, "sim_operator"));
        Assert.That(slaveSaved.Revision, Is.EqualTo(1));
        Assert.That(slave.Snapshot().Single().ChannelId, Is.EqualTo("CH02"));
        Assert.That(master.Snapshot().Single().ChannelId, Is.EqualTo("CH06"));
    }

    [Test]
    public void InvalidRoiAndUnsignedActorsNeverEnterSqlite()
    {
        var store = new RoiConfigStore(Path.Combine(_root, "config.sqlite"), MachineRole.Master);
        var valid = new RoiDraftEdit("CH06", .1, .1, .7, .7, 0, "sim_operator");
        Assert.Throws<InvalidDataException>(() => store.Save(valid with { Width = double.NaN }));
        Assert.Throws<InvalidDataException>(() => store.Save(valid with { Width = .99 }));
        Assert.Throws<InvalidDataException>(() => store.Save(valid with { X = -.1 }));
        Assert.Throws<InvalidDataException>(() => store.Save(valid with { ExpectedRevision = -1 }));
        Assert.Throws<InvalidDataException>(() => store.Save(valid with { Actor = "" }));
        Assert.That(store.Snapshot(), Is.Empty);
    }

    [Test]
    public void CorruptedForeignOwnedRowIsNotProjectedAsSafeLocalDraft()
    {
        var path = Path.Combine(_root, "config.sqlite");
        var store = new RoiConfigStore(path, MachineRole.Master);
        store.Save(new RoiDraftEdit("CH06", .1, .1, .7, .7, 0, "sim_operator"));
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var insert = db.CreateCommand();
            insert.CommandText = """
                INSERT INTO sim_roi_drafts
                (channel_id,x,y,width,height,revision,actor,edited_utc)
                VALUES ('CH02',0.1,0.1,0.7,0.7,1,'attacker',$utc)
                """;
            insert.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            Assert.That(insert.ExecuteNonQuery(), Is.EqualTo(1));
        }
        Assert.Throws<InvalidDataException>(() => store.Snapshot());
    }
}
