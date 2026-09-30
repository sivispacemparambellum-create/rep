// CardView.cs — Unity-представление карты: слои, состояния, анимация переворота.
// Base Layer (лицо/рубашка) + Modifier Layer (влияние поверх гангстера) + Token Layer (сделки поверх бизнеса).
using System.Collections.Generic;
using UnityEngine;
using LCN.Cards;
using LCN.Core;

namespace LCN.UI
{
    [RequireComponent(typeof(SpriteRenderer))]
    public class CardView : MonoBehaviour
    {
        [Header("Base Layer")]
        [SerializeField] private SpriteRenderer baseRenderer;        // лицо ИЛИ рубашка
        [Tooltip("Рубашки по типам колод — клиент видит ТОЛЬКО их для скрытых карт")]
        [SerializeField] private Sprite[] jobBacksByRound = new Sprite[4];   // Round I..IV
        [SerializeField] private Sprite influenceBack;
        [SerializeField] private Sprite businessBack;

        [Header("Modifier / Token Layers")]
        [SerializeField] private Transform modifierRoot;             // спрайты Henchman/MachinePistol
        [SerializeField] private Transform tokenRoot;                // маленькие DealToken-спрайты
        [SerializeField] private Sprite dealTokenSprite;
        [SerializeField] private Color dealTokenOwnerColors = Color.cyan; // в реальности — палитра по игроку

        [Header("Deactivated / Animation")]
        [SerializeField] private Material grayscaleOverlayMaterial;  // серый полупрозрачный фильтр
        [SerializeField] private float flipDuration = 0.45f;

        private CardInstance _model;
        private CardViewState _state = CardViewState.FaceDown;
        private bool _isFlipping;

        public CardInstance Model => _model;

        /// <summary>Привязка модели данных к представлению.</summary>
        public void Bind(CardInstance instance, PlayerController viewer)
        {
            _model = instance;
            ApplyState();
            RefreshModifiers();
            RefreshTokens(viewer);
        }

        // ================= СОСТОЯНИЯ =================

        public void SetState(CardViewState newState)
        {
            if (newState == _state) return;
            if (newState == CardViewState.FaceUp && _state == CardViewState.FaceDown) FlipFaceUp();
            else { _state = newState; ApplyState(); }
        }

        private void ApplyState()
        {
            switch (_state)
            {
                case CardViewState.FaceUp:
                    baseRenderer.sprite = LoadFront(_model);
                    SetGrayscale(false);
                    break;

                case CardViewState.FaceDown:
                    // ВАЖНО: рубашка зависит от типа колоды — игрок видит источник карты,
                    // но НЕ получает id/лицо скрытой карты (см. Security ниже).
                    baseRenderer.sprite = PickBack(_model);
                    SetGrayscale(false);
                    break;

                case CardViewState.Deactivated:
                    // Бизнес потерян: остаётся лицом вверх, но под серым фильтром —
                    // чтобы помнить, какой именно бизнес был уничтожен.
                    baseRenderer.sprite = LoadFront(_model);
                    SetGrayscale(true);
                    break;
            }
        }

        private Sprite PickBack(CardInstance c)
        {
            return c.Data switch
            {
                JobCard j => jobBacksByRound[(int)j.DeckRound],          // back_jobs_round1..4
                InfluenceCard => influenceBack,
                BusinessCard => businessBack,
                _ => businessBack
            };
        }

        // ================= SECURITY: ЛИЦО КАРТЫ НЕ УТЕКАЕТ КЛИЕНТУ =================

        /// <summary>Фронтальный спрайт грузится только если карта открыта ЭТОМУ наблюдателю.
        /// Для чужих закрытых дел GameManager присылает лишь enum рубашки (DeckRound),
        /// поэтому LoadFront для них даже не вызывается логикой выше.</summary>
        private Sprite LoadFront(CardInstance c)
        {
            if (!IsVisibleToObserver(c)) return PickBack(c); // defense-in-depth
            return SpriteLibrary.Get(c.Data.FrontSprite.Key);
        }

        protected virtual bool IsVisibleToObserver(CardInstance c) =>
            c.State != CardViewState.FaceDown || IsOwnCard(c);

        private bool IsOwnCard(CardInstance c)
        {
            var local = GameContext.LocalHuman;
            return local != null && (local.Hand.Jobs.Contains(c) || local.Hand.Influences.Contains(c));
        }

        // ================= АНИМАЦИЯ ПЕРЕВОРОТА (Reveal) =================

        private void FlipFaceUp()
        {
            if (_isFlipping) return;
            StartCoroutine(FlipRoutine());
        }

        private IEnumerator<UnityEngine.YieldInstruction> FlipRoutine()
        {
            _isFlipping = true;
            float t = 0f;
            Vector3 start = transform.localScale;
            while (t < flipDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Abs(Mathf.Cos(t / flipDuration * Mathf.PI)); // squash X
                transform.localScale = new Vector3(start.x * Mathf.Max(0.02f, k), start.y, start.z);
                if (t >= flipDuration / 2 && _state != CardViewState.FaceUp)
                {
                    _state = CardViewState.FaceUp;
                    ApplyState(); // смена спрайта ровно на «ребре» карты
                }
                yield return null;
            }
            transform.localScale = start;
            _isFlipping = false;
        }

        /// <summary>GameManager вызывает это событие для запуска reveal в фазе Action.</summary>
        public void OnSubscribedJobRevealed(CardInstance revealed)
        {
            if (revealed == _model) SetState(CardViewState.FaceUp);
        }

        // ================= MODIFIER LAYER =================

        private readonly List<SpriteRenderer> _modifierPool = new();

        private void RefreshModifiers()
        {
            foreach (var r in _modifierPool) r.gameObject.SetActive(false);
            if (_model?.AttachedInfluences == null) return;

            int i = 0;
            foreach (var inf in _model.AttachedInfluences)
            {
                var sr = GetPoolSlot(ref i);
                sr.sprite = SpriteLibrary.Get($"influence/{inf.Effect.ToString().ToLowerInvariant()}");
                sr.color = Color.white;
                sr.transform.localPosition = new Vector3(-0.35f + 0.3f * i, 0.45f, -0.1f); // поверх арта гангстера
                sr.gameObject.SetActive(true);
            }
        }

        private SpriteRenderer GetPoolSlot(ref int index)
        {
            while (_modifierPool.Count <= index)
            {
                var go = new GameObject($"Mod_{_modifierPool.Count}");
                go.transform.SetParent(modifierRoot, false);
                _modifierPool.Add(go.AddComponent<SpriteRenderer>());
            }
            return _modifierPool[index++];
        }

        // ================= TOKEN LAYER (маркеры сделок поверх бизнеса) =================

        private readonly List<SpriteRenderer> _tokenPool = new();

        public void RefreshTokens(PlayerController viewer)
        {
            foreach (var r in _tokenPool) r.gameObject.SetActive(false);
            var tokens = _model?.TokensOnIt;
            if (tokens == null) return;

            int i = 0;
            foreach (var t in tokens)
            {
                // Статус определяет прозрачность: Accepted — яркий, Offered — мигающий, Expired — тусклый
                var sr = GetTokenSlot(ref i);
                sr.sprite = dealTokenSprite;
                sr.color = t.Status switch
                {
                    DealStatus.Accepted => OwnerColor(t.OwnerId),
                    DealStatus.Offered  => WithAlpha(OwnerColor(t.OwnerId), 0.5f),
                    DealStatus.Expired  => WithAlpha(Color.gray, 0.35f),
                    _ => new Color(1, 1, 1, 0f)
                };
                sr.transform.localPosition = new Vector3(0.38f - 0.16f * i, -0.42f, -0.1f); // нижний край бизнеса
                sr.transform.localScale = Vector3.one * 0.35f;                              // маленький спрайт
                sr.gameObject.SetActive(true);
            }
        }

        private static Color WithAlpha(Color c, float a) { c.a = a; return c; }

        private Color OwnerColor(string playerId)
        {
            var players = GameContext.State?.Players;
            int idx = players?.FindIndex(p => p.Id == playerId) ?? 0;
            return GameContext.PlayerPalette[Mathf.Clamp(idx, 0, GameContext.PlayerPalette.Length - 1)];
        }

        private SpriteRenderer GetTokenSlot(ref int index)
        {
            while (_tokenPool.Count <= index)
            {
                var go = new GameObject($"Tok_{_tokenPool.Count}");
                go.transform.SetParent(tokenRoot, false);
                _tokenPool.Add(go.AddComponent<SpriteRenderer>());
            }
            return _tokenPool[index++];
        }

        // ================= GRAYSCALE OVERLAY =================

        private void SetGrayscale(bool on)
        {
            if (grayscaleOverlayMaterial == null) return;
            baseRenderer.sharedMaterial = on ? grayscaleOverlayMaterial : null;
        }

        private void OnEnable()  { GameEvents.JobRevealed += OnSubscribedJobRevealed; }
        private void OnDisable() { GameEvents.JobRevealed -= OnSubscribedJobRevealed; }
    }

    /// <summary>Контекст UI: ссылки на активную партию и локального человека.</summary>
    public static class GameContext
    {
        public static GameState State => LCN.Core.GameState.Current;
        public static PlayerController LocalHuman;
        public static Color[] PlayerPalette =
        {
            new Color(0.9f, 0.2f, 0.2f), new Color(0.2f, 0.7f, 0.3f),
            new Color(0.2f, 0.4f, 0.9f), new Color(0.9f, 0.8f, 0.2f),
            new Color(0.7f, 0.3f, 0.9f)
        };
    }

    /// <summary>Кэш спрайтов (Addressables/Resources — на выбор проекта).</summary>
    public static class SpriteLibrary
    {
        private static readonly Dictionary<string, Sprite> _cache = new();

        public static Sprite Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (!_cache.TryGetValue(key, out var s))
            {
                // Вариант A: Resources.Load<Sprite>($"Cards/{key}")
                // Вариант B: Addressables.LoadAssetAsync<Sprite>(key) — заполняет кэш колбэком
                s = Resources.Load<Sprite>($"Cards/{key}");
                _cache[key] = s;
            }
            return s;
        }
    }
}
