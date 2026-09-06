using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace WheatStook;

public sealed class PrairieKingBot
{
    internal static int[] BotMoveDirections = Array.Empty<int>();
    internal static int[] BotShootDirections = Array.Empty<int>();
    internal static bool BotInjecting;

    private bool _active;
    private string? _lastError;
    private int _errorCooldown;
    private Vector2 _lastMove;
    private bool _harmonyPatched;

    private static Type? _abigailGameType;
    private static Type? _abigailGameReflectedType;
    private static FieldInfo? _playerPositionField;
    private static FieldInfo? _playerBoundingBoxField;
    private static FieldInfo? _playerMovementDirectionsField;
    private static FieldInfo? _playerShootingDirectionsField;
    private static FieldInfo? _monstersField;
    private static FieldInfo? _bulletsField;
    private static FieldInfo? _powerupsField;
    private static FieldInfo? _shoppingField;
    private static FieldInfo? _deathTimerField;
    private static FieldInfo? _betweenWaveTimerField;
    private static FieldInfo? _gameOverField;
    private static FieldInfo? _livesField;
    private static FieldInfo? _coinsField;
    private static FieldInfo? _whichWaveField;
    private static readonly Dictionary<string, FieldInfo?> _objectFieldCache = new();

    public bool IsActive => _active;
    public string? LastError => _lastError;
    public Vector2 LastMove => _lastMove;

    public void Start()
    {
        _active = true;
        _lastError = null;
    }

    public void Stop()
    {
        var minigame = Game1.currentMinigame;
        if (IsPrairieKing(minigame))
            ClearInput(minigame);
        _active = false;
    }

    public void Update(IMonitor monitor)
    {
        if (IsPrairieKing(Game1.currentMinigame))
        {
            if (!_active)
            {
                _active = true;
                _lastError = null;
                monitor.Log("Prairie King detected — bot auto-started", LogLevel.Info);
            }
            if (!_harmonyPatched)
            {
                try
                {
                    var harmony = new Harmony("WheatStook.PrairieKingBot");
                    var agType = Game1.currentMinigame!.GetType();
                    var updateInput = agType.GetMethod("_UpdateInput", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? agType.GetMethod("UpdateInput", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (updateInput != null)
                    {
                        harmony.Patch(updateInput, postfix: new HarmonyMethod(typeof(PrairieKingBot), nameof(InputPostfix)));
                        monitor.Log($"Harmony patched {updateInput.Name} for bot input injection", LogLevel.Info);
                    }
                    else
                        monitor.Log("Could not find _UpdateInput method to patch", LogLevel.Warn);
                    _harmonyPatched = true;
                }
                catch (Exception ex)
                {
                    monitor.Log($"Harmony patch failed: {ex.Message}", LogLevel.Error);
                    _harmonyPatched = true;
                }
            }
            try
            {
                Tick(Game1.currentMinigame!);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                if (_errorCooldown <= 0)
                {
                    monitor.Log($"Prairie King bot error: {ex}", LogLevel.Warn);
                    _errorCooldown = 300;
                }
            }
        }
        else if (_active)
        {
            _active = false;
            BotInjecting = false;
            BotMoveDirections = Array.Empty<int>();
            BotShootDirections = Array.Empty<int>();
        }

        if (_errorCooldown > 0)
            _errorCooldown--;
    }

    public object BuildState()
    {
        var minigame = Game1.currentMinigame;
        if (!IsPrairieKing(minigame))
        {
            return new
            {
                ok = true,
                active = false,
                botActive = _active,
                currentMinigame = minigame?.GetType().FullName
            };
        }

        EnsureReflection(minigame!);
        var playerBounds = ReadPlayerBounds(minigame!);
        var monsters = ReadMonsters(minigame!);
        var bullets = ReadBullets(minigame!);
        var powerups = ReadPowerups(minigame!);

        return new
        {
            ok = true,
            active = true,
            botActive = _active,
            currentMinigame = minigame!.GetType().FullName,
            flags = new
            {
                shopping = IsShopping(minigame),
                death = IsDeathState(minigame),
                betweenWaves = IsBetweenWaves(minigame, monsters.Count),
                gameOver = ReadBoolField(minigame, _gameOverField)
            },
            player = new
            {
                x = playerBounds.Center.X,
                y = playerBounds.Center.Y,
                bounds = new { playerBounds.X, playerBounds.Y, playerBounds.Width, playerBounds.Height },
                lives = ReadIntField(minigame, _livesField, -1),
                coins = ReadIntField(minigame, _coinsField, -1),
                wave = ReadIntField(minigame, _whichWaveField, -1)
            },
            counts = new { monsters = monsters.Count, bullets = bullets.Count, powerups = powerups.Count },
            monsters = monsters.Select(m => new
            {
                x = m.Bounds.Center.X,
                y = m.Bounds.Center.Y,
                bounds = new { m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height },
                m.Health,
                m.Type,
                m.Speed
            }).ToList(),
            bullets = bullets.Select(b => new
            {
                x = b.Bounds.Center.X,
                y = b.Bounds.Center.Y,
                bounds = new { b.Bounds.X, b.Bounds.Y, b.Bounds.Width, b.Bounds.Height },
                motion = new { x = b.Motion.X, y = b.Motion.Y },
                b.Damage
            }).ToList(),
            powerups = powerups.Select(p => new
            {
                x = p.Bounds.Center.X,
                y = p.Bounds.Center.Y,
                bounds = new { p.Bounds.X, p.Bounds.Y, p.Bounds.Width, p.Bounds.Height },
                p.Which
            }).ToList(),
            inputFields = new
            {
                movement = _playerMovementDirectionsField?.Name,
                shooting = _playerShootingDirectionsField?.Name,
                allFields = minigame!.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(f => f.FieldType.Name.Contains("List") || f.Name.Contains("irection") || f.Name.Contains("move") || f.Name.Contains("shoot") || f.Name.Contains("input") || f.Name.Contains("key"))
                    .Select(f => $"{f.FieldType.Name} {f.Name}")
                    .ToArray(),
                allMethods = minigame!.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.Contains("Input") || m.Name.Contains("Update") || m.Name.Contains("key") || m.Name.Contains("Key"))
                    .Select(m => m.Name)
                    .Distinct()
                    .ToArray()
            },
            lastMove = new { x = _lastMove.X, y = _lastMove.Y },
            lastError = _lastError
        };
    }

    private void Tick(object game)
    {
        EnsureReflection(game);

        if (IsShopping(game) || IsDeathState(game))
        {
            ClearInput(game);
            return;
        }

        var playerBounds = ReadPlayerBounds(game);
        if (playerBounds.Width <= 0 || playerBounds.Height <= 0)
        {
            ClearInput(game);
            return;
        }

        var monsters = ReadMonsters(game);
        var bullets = ReadBullets(game);
        var powerups = ReadPowerups(game);

        if (IsBetweenWaves(game, monsters.Count) && powerups.Count == 0)
        {
            ClearInput(game);
            return;
        }

        var player = new Vector2(playerBounds.Center.X, playerBounds.Center.Y);
        var move = ChooseMove(player, monsters, bullets, powerups);
        _lastMove = move;

        if (move != Vector2.Zero && _playerPositionField != null)
        {
            var pos = _playerPositionField.GetValue(game);
            if (pos is Vector2 currentPos)
            {
                var speed = 3f;
                var newPos = currentPos + move * speed;
                newPos.X = Math.Clamp(newPos.X, 8f, 744f);
                newPos.Y = Math.Clamp(newPos.Y, 8f, 744f);
                _playerPositionField.SetValue(game, newPos);

                if (_playerBoundingBoxField != null)
                {
                    var bb = new Rectangle(
                        (int)newPos.X - 12, (int)newPos.Y - 12, 24, 24);
                    _playerBoundingBoxField.SetValue(game, bb);
                }
            }
        }

        var target = monsters
            .Where(m => m.Health != 0)
            .OrderBy(m => Vector2.DistanceSquared(player, new Vector2(m.Bounds.Center.X, m.Bounds.Center.Y)))
            .FirstOrDefault();

        var shootDirs = target != null
            ? DirectionsFromVector(new Vector2(target.Bounds.Center.X - player.X, target.Bounds.Center.Y - player.Y), 0.15f)
            : Array.Empty<int>();

        BotShootDirections = shootDirs;
        BotMoveDirections = DirectionsFromVector(move, 0.25f);
        BotInjecting = true;
        _lastError = null;
    }

    private static Vector2 ChooseMove(
        Vector2 player,
        List<Monster> monsters,
        List<Bullet> bullets,
        List<Powerup> powerups)
    {
        var candidates = new[]
        {
            Vector2.Zero,
            new Vector2(0, -1),
            new Vector2(1, -1),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1),
            new Vector2(-1, 1),
            new Vector2(-1, 0),
            new Vector2(-1, -1)
        };

        var best = Vector2.Zero;
        var bestScore = double.NegativeInfinity;

        foreach (var raw in candidates)
        {
            var move = raw;
            if (move != Vector2.Zero)
                move.Normalize();

            var probe = player + move * 42f;
            var score = 0.0;

            if (probe.X < 48 || probe.X > 720 || probe.Y < 48 || probe.Y > 720)
                score -= 20000;

            var center = new Vector2(384, 384);
            score -= Vector2.Distance(probe, center) * 0.03;

            foreach (var bullet in bullets)
            {
                var bulletPos = new Vector2(bullet.Bounds.Center.X, bullet.Bounds.Center.Y);
                var motion = new Vector2(bullet.Motion.X, bullet.Motion.Y);
                var worst = double.PositiveInfinity;

                for (int i = 0; i <= 18; i += 3)
                {
                    var future = bulletPos + motion * i;
                    worst = Math.Min(worst, Vector2.Distance(probe, future));
                }

                if (worst < 34)
                    score -= 50000;
                else if (worst < 96)
                    score -= (96 - worst) * 45;

                var towardPlayer = player - bulletPos;
                if (motion != Vector2.Zero && towardPlayer != Vector2.Zero)
                {
                    motion.Normalize();
                    towardPlayer.Normalize();
                    score -= Math.Max(0, Vector2.Dot(motion, towardPlayer)) * 350;
                }
            }

            foreach (var monster in monsters)
            {
                var monsterPos = new Vector2(monster.Bounds.Center.X, monster.Bounds.Center.Y);
                var dist = Vector2.Distance(probe, monsterPos);
                if (dist < 58)
                    score -= 35000;
                else if (dist < 150)
                    score -= (150 - dist) * 85;
                else if (dist > 230)
                    score += Math.Min(90, (dist - 230) * 0.2);
            }

            foreach (var powerup in powerups)
            {
                var powerupPos = new Vector2(powerup.Bounds.Center.X, powerup.Bounds.Center.Y);
                var powerupDistance = Vector2.Distance(probe, powerupPos);
                var nearestMonster = monsters.Count == 0
                    ? double.PositiveInfinity
                    : monsters.Min(m => Vector2.Distance(powerupPos, new Vector2(m.Bounds.Center.X, m.Bounds.Center.Y)));
                var nearestBullet = bullets.Count == 0
                    ? double.PositiveInfinity
                    : bullets.Min(b => Vector2.Distance(powerupPos, new Vector2(b.Bounds.Center.X, b.Bounds.Center.Y)));

                if (nearestMonster > 115 && nearestBullet > 80)
                    score += 1600 / Math.Max(1, powerupDistance);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = move;
            }
        }

        return best;
    }

    internal static void InputPostfix(object __instance)
    {
        if (!BotInjecting) return;
        var type = __instance.GetType();
        var moveField = type.GetField("player2MovementDirections", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type.GetField("playerMovementDirections", BindingFlags.NonPublic | BindingFlags.Instance);
        var shootField = type.GetField("player2ShootingDirections", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type.GetField("playerShootingDirections", BindingFlags.NonPublic | BindingFlags.Instance);
        if (moveField?.GetValue(__instance) is ICollection<int> moveList)
        {
            moveList.Clear();
            foreach (var d in BotMoveDirections) moveList.Add(d);
        }
        if (shootField?.GetValue(__instance) is ICollection<int> shootList)
        {
            shootList.Clear();
            foreach (var d in BotShootDirections) shootList.Add(d);
        }
    }

    private static void ClearInput(object? game)
    {
        BotInjecting = false;
        BotMoveDirections = Array.Empty<int>();
        BotShootDirections = Array.Empty<int>();
    }

    private static void SetDirections(object game, FieldInfo? field, IEnumerable<int> directions)
    {
        if (field == null)
            return;

        var dirArray = directions.Distinct().ToArray();
        var target = field.IsStatic ? null : game;
        var current = field.GetValue(target);

        if (current is ICollection<int> intCollection)
        {
            intCollection.Clear();
            foreach (var direction in dirArray)
                intCollection.Add(direction);
            return;
        }

        if (field.FieldType == typeof(int[]))
        {
            field.SetValue(target, dirArray);
            return;
        }

        if (field.FieldType.IsAssignableFrom(typeof(List<int>)))
            field.SetValue(target, dirArray.ToList());
    }

    private static int[] DirectionsFromVector(Vector2 vector, float threshold)
    {
        if (vector == Vector2.Zero)
            return Array.Empty<int>();

        if (vector.LengthSquared() > 1f)
            vector.Normalize();

        var directions = new List<int>(2);
        if (vector.Y < -threshold)
            directions.Add(0);
        if (vector.X > threshold)
            directions.Add(1);
        if (vector.Y > threshold)
            directions.Add(2);
        if (vector.X < -threshold)
            directions.Add(3);

        if (directions.Count > 0)
            return directions.ToArray();

        return Math.Abs(vector.X) > Math.Abs(vector.Y)
            ? new[] { vector.X >= 0 ? 1 : 3 }
            : new[] { vector.Y >= 0 ? 2 : 0 };
    }

    public static bool IsPrairieKing(object? minigame)
    {
        if (minigame == null)
            return false;

        _abigailGameType ??= typeof(Game1).Assembly.GetType("StardewValley.Minigames.AbigailGame");
        return minigame.GetType() == _abigailGameType
            || minigame.GetType().FullName == "StardewValley.Minigames.AbigailGame";
    }

    private static void EnsureReflection(object game)
    {
        var type = game.GetType();
        if (_abigailGameReflectedType == type)
            return;

        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags statik = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        _abigailGameType = type;
        _abigailGameReflectedType = type;
        _objectFieldCache.Clear();

        _playerPositionField = FirstField(type, instance, "playerPosition", "playerPos");
        _playerBoundingBoxField = FirstField(type, instance, "playerBoundingBox", "playerBox", "playerBounds");
        _playerMovementDirectionsField = FirstField(type, instance, "playerMovementDirections", "player2MovementDirections");
        _playerShootingDirectionsField = FirstField(type, instance, "playerShootingDirections", "player2ShootingDirections");
        _monstersField = FirstField(type, statik, "monsters")
            ?? FindCollectionField(type, "CowboyMonster", true);
        _bulletsField = FirstField(type, instance, "enemyBullets", "monsterBullets", "bullets")
            ?? FindCollectionField(type, "CowboyBullet", false);
        _powerupsField = FirstField(type, instance, "powerups")
            ?? FindCollectionField(type, "CowboyPowerup", false);
        _shoppingField = FirstField(type, instance, "shopping", "isShopping");
        _deathTimerField = FirstField(type, instance, "deathTimer", "playerDeathTimer", "playerDieTimer", "diedTimer");
        _betweenWaveTimerField = FirstField(type, instance, "betweenWaveTimer", "newWaveTimer", "waveStartTimer");
        _gameOverField = FirstField(type, instance, "gameOver", "gameOverScreen");
        _livesField = FirstField(type, instance, "lives", "playerLives");
        _coinsField = FirstField(type, instance, "coins", "money");
        _whichWaveField = FirstField(type, instance, "whichWave", "currentWave", "wave");
    }

    private static FieldInfo? FirstField(Type type, BindingFlags flags, params string[] names)
    {
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field;
        }
        return null;
    }

    private static FieldInfo? FindCollectionField(Type type, string elementTypeName, bool includeStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (includeStatic)
            flags |= BindingFlags.Static;

        return type.GetFields(flags)
            .FirstOrDefault(field => FieldMentionsType(field.FieldType, elementTypeName));
    }

    private static bool FieldMentionsType(Type type, string typeName)
    {
        if ((type.FullName ?? type.Name).Contains(typeName, StringComparison.Ordinal))
            return true;

        return type.IsGenericType && type.GetGenericArguments()
            .Any(arg => (arg.FullName ?? arg.Name).Contains(typeName, StringComparison.Ordinal));
    }

    private static bool IsShopping(object game)
    {
        return ReadBoolField(game, _shoppingField);
    }

    private static bool IsDeathState(object game)
    {
        return ReadBoolField(game, _gameOverField) || ReadIntField(game, _deathTimerField, 0) > 0;
    }

    private static bool IsBetweenWaves(object game, int monsterCount)
    {
        return monsterCount == 0 || ReadIntField(game, _betweenWaveTimerField, 0) > 0;
    }

    private static Rectangle ReadPlayerBounds(object game)
    {
        var target = _playerBoundingBoxField?.GetValue(game);
        if (TryReadRectangle(target, out var rect))
            return rect;

        target = _playerPositionField?.GetValue(game);
        if (TryReadPoint(target, out var point))
            return new Rectangle(point.X - 16, point.Y - 16, 32, 32);

        return Rectangle.Empty;
    }

    private static List<Monster> ReadMonsters(object game)
    {
        return ReadObjects(game, _monstersField)
            .Select(obj => new Monster(
                ReadObjectRectangle(obj, "position", 32),
                ReadObjectInt(obj, "health", 1),
                ReadObjectInt(obj, "type", -1),
                ReadObjectInt(obj, "speed", 0)))
            .Where(m => m.Bounds.Width > 0 && m.Bounds.Height > 0 && m.Health != 0)
            .ToList();
    }

    private static List<Bullet> ReadBullets(object game)
    {
        return ReadObjects(game, _bulletsField)
            .Select(obj => new Bullet(
                ReadObjectRectangle(obj, "position", 12),
                ReadObjectPoint(obj, "motion"),
                ReadObjectInt(obj, "damage", 1)))
            .Where(b => b.Bounds.Width > 0 && b.Bounds.Height > 0)
            .ToList();
    }

    private static List<Powerup> ReadPowerups(object game)
    {
        return ReadObjects(game, _powerupsField)
            .Select(obj => new Powerup(
                ReadObjectRectangle(obj, "position", 24),
                ReadObjectInt(obj, "which", -1)))
            .Where(p => p.Bounds.Width > 0 && p.Bounds.Height > 0)
            .ToList();
    }

    private static List<object> ReadObjects(object game, FieldInfo? field)
    {
        if (field == null)
            return new List<object>();

        var value = field.GetValue(field.IsStatic ? null : game);
        if (value is not System.Collections.IEnumerable enumerable)
            return new List<object>();

        var result = new List<object>();
        foreach (var entry in enumerable)
        {
            if (entry != null)
                result.Add(entry);
        }
        return result;
    }

    private static Rectangle ReadObjectRectangle(object obj, string memberName, int fallbackSize)
    {
        var value = ReadObjectMember(obj, memberName);
        if (TryReadRectangle(value, out var rect))
            return rect;
        if (TryReadPoint(value, out var point))
            return new Rectangle(point.X - fallbackSize / 2, point.Y - fallbackSize / 2, fallbackSize, fallbackSize);
        return Rectangle.Empty;
    }

    private static Point ReadObjectPoint(object obj, string memberName)
    {
        var value = ReadObjectMember(obj, memberName);
        return TryReadPoint(value, out var point) ? point : Point.Zero;
    }

    private static int ReadObjectInt(object obj, string memberName, int fallback)
    {
        var value = ReadObjectMember(obj, memberName);
        try { return value == null ? fallback : Convert.ToInt32(value); }
        catch { return fallback; }
    }

    private static object? ReadObjectMember(object obj, string memberName)
    {
        var type = obj.GetType();
        var key = $"{type.FullName}.{memberName}";
        if (!_objectFieldCache.TryGetValue(key, out var field))
        {
            field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _objectFieldCache[key] = field;
        }

        if (field != null)
            return field.GetValue(obj);

        return type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj);
    }

    private static bool TryReadRectangle(object? value, out Rectangle rect)
    {
        if (value is Rectangle xnaRect)
        {
            rect = xnaRect;
            return true;
        }

        rect = Rectangle.Empty;
        return false;
    }

    private static bool TryReadPoint(object? value, out Point point)
    {
        switch (value)
        {
            case Point xnaPoint:
                point = xnaPoint;
                return true;
            case Vector2 vector:
                point = new Point((int)vector.X, (int)vector.Y);
                return true;
            default:
                point = Point.Zero;
                return false;
        }
    }

    private static int ReadIntField(object owner, FieldInfo? field, int fallback)
    {
        if (field == null)
            return fallback;

        try
        {
            var value = field.GetValue(field.IsStatic ? null : owner);
            return value == null ? fallback : Convert.ToInt32(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBoolField(object owner, FieldInfo? field)
    {
        if (field == null)
            return false;

        try
        {
            var value = field.GetValue(field.IsStatic ? null : owner);
            return value is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private sealed record Monster(Rectangle Bounds, int Health, int Type, int Speed);
    private sealed record Bullet(Rectangle Bounds, Point Motion, int Damage);
    private sealed record Powerup(Rectangle Bounds, int Which);
}
