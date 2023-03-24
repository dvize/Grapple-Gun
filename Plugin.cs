using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace GrappleGun
{

    [BepInPlugin("com.dvize.GrappleGun", "dvize.GrappleGun", "1.1.0")]
    class GrappleGunPlugin : BaseUnityPlugin
    {

        public LayerMask ObjectLayer = LayerMaskClass.layerMask_0;
        
        public static ConfigEntry<KeyboardShortcut> GrappleKey
        {
            get; set;
        }
        public static ConfigEntry<float> maxConnectionDistance
        {
            get; set;
        }
        public static ConfigEntry<float> maxSpeed
        {
            get; set;
        }
        public static ConfigEntry<float> durationMultiplier
        {
            get; set;
        }
        public static ConfigEntry<float> maxAcceleration
        {
            get; set;
        }
        public static ConfigEntry<float> distanceAboveHook
        {
            get; set;
        }
        public static ConfigEntry<Vector3> gravityVector
        {
            get; set;
        }
        public static ConfigEntry<Boolean> emergencyResetPosition
        {
            get; set;
        }

        public static bool isGrappling = false;

        private LineRenderer grapplingHookLine;
        private Vector3 grapplingHookPosition;
        private Vector3 grappleDirection;

        public static AudioClip ShootGrappleGunClip;
        public static AudioClip DetachGrappleGunClip;
        public static Vector3 spawnPosition;
        public async void Awake()
        {
            GrappleKey = Config.Bind(
                "Main Settings",
                "Grapple Key",
                new KeyboardShortcut(KeyCode.Mouse4),
                "Key for Grapple Shooting/Detaching");

            maxConnectionDistance = Config.Bind(
                "Main Settings",
                "maxConnectionDistance",
                150f,
                "Max Distance to Attach Hook");

            durationMultiplier = Config.Bind(
                "Main Settings",
                "Duration Multiplier",
                1f,
                "Multiplies the hooking distance in calc so you hang time longer");

            maxSpeed = Config.Bind(
                "Main Settings",
                "maxSpeed",
                40f,
                "Max Speed it accelerates up to when using Hook");

            maxAcceleration = Config.Bind(
                "Main Settings",
                "maxAcceleration",
                10f,
                "Max Acceleration when using Hook");

            distanceAboveHook = Config.Bind(
                "Main Settings",
                "Distance Above Hook Anchor",
                22f,
                "How far above hook you land");

            gravityVector = Config.Bind(
                "Main Settings",
                "Gravity When Using Grapple",
                new Vector3(0f,-1f,0f),
                "normal gravity is 0f, -9.8, 0f");

            emergencyResetPosition = Config.Bind(
                "Main Settings",
                "Reset to Start Position",
                false,
                "Reset to start position");

            string uri = "file://" + (BepInEx.Paths.PluginPath + "\\dvize.GrappleGun\\hook_start.ogg");
            string uri2 = "file://" + (BepInEx.Paths.PluginPath + "\\dvize.GrappleGun\\hook_end.ogg");

            ShootGrappleGunClip = await LoadAudioClip(uri);
            DetachGrappleGunClip = await LoadAudioClip(uri2);
        }

        private static bool runOnceOnly = false;
        private static bool gameStarted = true;
        private static bool firstTimeTriggered = false;
        private static float toggleTimer;
        private void Update()
        {
            try
            {
                var game = Singleton<AbstractGame>.Instance;
                Player player = Singleton<GameWorld>.Instance.MainPlayer;

                if (game.InRaid && Camera.main != null)
                {
                    if (gameStarted)
                    {
                        if (!runOnceOnly)
                        {
                            Logger.LogDebug("GG: Set all events");
                            player.OnPlayerDeadOrUnspawn += Player_OnPlayerDeadOrUnspawn;
                            spawnPosition = player.Transform.position;
                            player.ActiveHealthController.FallSafeHeight = 999999f;
                            isCoroutineRunning = false;
                            isGrappling = false;
                            firstTimeTriggered = false;
                            canMove = true;
                            runOnceOnly = true;
                            toggleTimer = 0;
                        }
                        if (GrappleKey.Value.IsUp() && isGrappling)
                        {
                            Logger.LogDebug("GG: End Grapple Hook");
                            StopGrapple(player, player.Transform.position);
                            isGrappling = false;
                            firstTimeTriggered = false;
                            //fix gravity
                            setLessGravity(player, false);
                            Singleton<GUISounds>.Instance.PlaySound(DetachGrappleGunClip);
                        }
                        if (GrappleKey.Value.IsDown() && Time.time - toggleTimer >= 1f)
                        {
                            Logger.LogDebug("GG: Start Grapple Hook");
                            StartGrapple(player);
                            isGrappling = true;
                            firstTimeTriggered = true;
                            //fix gravity
                            setLessGravity(player, true);
                            Singleton<GUISounds>.Instance.PlaySound(ShootGrappleGunClip);
                            toggleTimer = Time.time;
                        }
                        if (emergencyResetPosition.Value == true)
                        {
                            StopGrapple(player, player.Transform.position);
                            player.Transform.position = spawnPosition;
                            isGrappling = false;
                            firstTimeTriggered = false;
                            //fix gravity
                            setLessGravity(player, false);
                        }

                    }

                }
            }

            catch { }
            
        }

        private void Player_OnPlayerDeadOrUnspawn(Player player)
        {
            Logger.LogDebug("GG: Undo all events");
            player.OnPlayerDeadOrUnspawn -= Player_OnPlayerDeadOrUnspawn;
            runOnceOnly = false;
            gameStarted = false;
            isGrappling = false;
            firstTimeTriggered = false;

            Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                // Set newGame = true after the timer is finished so it doesn't execute the events right away
                gameStarted = true;
            });
        }

        private float currentSpeed = 0f;
        private Collider[] myColliders;
        Vector3 capsuleStart;
        Vector3 capsuleEnd;
        CapsuleCollider playerCollider;
        bool isStillOnGround;
        bool levelBorderCollided;
        bool objectCollided;
        bool canMove = true;
        bool isCoroutineRunning;
        List<String> exclusionList = new List<string> {"body", "human", "root", "road", "terrain", "floor", "ballistic", "area", "rails", "tunel", "balistic", "spawn", "around"};
        private void FixedUpdate()
        {

            try
            {
                var game = Singleton<AbstractGame>.Instance;
                Player player = Singleton<GameWorld>.Instance.MainPlayer;

                if (game.InRaid && Camera.main != null)
                {

                    if (isGrappling)
                    {
                        isStillOnGround = isOnGround(player);
                            
                        //setup player collider
                        playerCollider = player.gameObject.GetComponent<CapsuleCollider>();

                        capsuleStart = playerCollider.bounds.center + Vector3.up * (playerCollider.height * 0.5f - playerCollider.radius);
                        capsuleEnd = playerCollider.bounds.center + Vector3.down * (playerCollider.height * 0.5f - playerCollider.radius);

                        myColliders = Physics.OverlapCapsule(capsuleStart, capsuleEnd, playerCollider.radius, ObjectLayer);

                        //setup detection bools
                        levelBorderCollided = false;
                        objectCollided = false;

                        canMove = true;

                        //check each collider and try to eliminate false positive where you next to a collider but on the ground
                        foreach (Collider collider in myColliders)
                        {
                            if (!isStillOnGround && collider.gameObject.name.ToLower() == "levelborder")
                            {
                                Logger.LogDebug("GG: Player Collider hit level border: " + collider.gameObject.transform.parent.name);
                                levelBorderCollided = true;
                            }
                            if (!exclusionList.Any(item => collider.gameObject.transform.parent.name.ToLower().Contains(item.ToLower()))
                                && ((ObjectLayer.value & 1 << collider.gameObject.layer) == 1 << collider.gameObject.layer))
                            {
                                Logger.LogDebug("GG: Player Collider hit object: " + collider.gameObject.transform.parent.name);
                                objectCollided = true;
                            }
                        }
                        
                        if (objectCollided || levelBorderCollided)
                        {
                            //Logger.LogDebug("GG: Hitting an Object Collider" or "GG: Hitting the Level Border");
                            StopGrapple(player, player.Transform.position);

                            canMove = false;
                        }
                        else
                        {
                            if (canMove && !isCoroutineRunning)
                            {
                                // Start the coroutine only if it's not already running
                                var startingPosition = player.Transform.position;
                                StartCoroutine(MovePlayerAlongParabolicPath(player, grapplingHookPosition, startingPosition));
                            }

                        }

                    }    
                }
            }
            catch { }
        }
        private IEnumerator MovePlayerAlongParabolicPath(Player player, Vector3 grapplingHookPosition, Vector3 startingPosition)
        {
            isCoroutineRunning = true;
            float distance = Vector3.Distance(grapplingHookPosition, startingPosition);
            float height = Mathf.Clamp(distance / 2f, 1f, GrappleGunPlugin.distanceAboveHook.Value);
            Vector3 apex = grapplingHookPosition + (startingPosition - grapplingHookPosition) / 2f + Vector3.up * height;

            float maxSpeed = GrappleGunPlugin.maxSpeed.Value;
            float maxAcceleration = GrappleGunPlugin.maxAcceleration.Value;
            float currentSpeed = 0f;

            float timeNeeded = Mathf.Abs(maxSpeed - currentSpeed) / maxAcceleration;
            float duration = (GrappleGunPlugin.durationMultiplier.Value * distance) / timeNeeded;
            Logger.LogDebug("GrappleGun: Moving player along parabolic path for " + duration + " seconds");

            float elapsedTime = 0f;
            while (elapsedTime < duration)
            {
                currentSpeed = Mathf.MoveTowards(currentSpeed, maxSpeed, Time.fixedDeltaTime * maxAcceleration);
                float timeRatio = elapsedTime / duration;
                Vector3 newPositionOnPath = CalculatePositionOnParabolicPath(grapplingHookPosition, apex, startingPosition, timeRatio);
                player.Transform.position = newPositionOnPath;
                elapsedTime += Time.fixedDeltaTime;
                yield return null;
            }

            //player.Transform.position = startingPosition;
            StopGrapple(player, startingPosition);
            //isCoroutineRunning = false;
        }

        private Vector3 CalculatePositionOnParabolicPath(Vector3 start, Vector3 apex, Vector3 end, float t)
        {
            return Mathf.Pow(1 - t, 2) * end + 2 * (1 - t) * t * apex + Mathf.Pow(t, 2) * start;
        }

        private void StartGrapple(Player player)
        {
            // Raycast from the mouse cursor (Center of screen) to find a hookable object
            Vector3 center = new Vector3(Screen.width / 2f, Screen.height / 2f, 0f);
            Ray ray = Camera.main.ScreenPointToRay(center);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, maxConnectionDistance.Value, ObjectLayer))
            {
                grapplingHookPosition = hit.point;
                grappleDirection = grapplingHookPosition - player.Transform.position;

                // Initialize Line Renderer
                grapplingHookLine = new GameObject("GrapplingHook").AddComponent<LineRenderer>();
                grapplingHookLine.positionCount = 2;
                grapplingHookLine.SetPosition(0, player.Transform.position);
                grapplingHookLine.SetPosition(1, grapplingHookPosition);
                grapplingHookLine.startWidth = 0.1f;
                grapplingHookLine.endWidth = 0.1f;
                grapplingHookLine.material = new Material(Shader.Find("Sprites/Default"));
                grapplingHookLine.startColor = Color.yellow;
                grapplingHookLine.endColor = Color.yellow;

                isGrappling = true;
            }
        }

        private void StopGrapple(Player player, Vector3 newPosition)
        {
            try
            {
                // Destroy Line Renderer and reset isGrappling flag
                Logger.LogDebug("GG: Stopping Grappling");
                Destroy(grapplingHookLine.gameObject);
                isGrappling = false;
                firstTimeTriggered = false;
                setLessGravity(player, false);
                isCoroutineRunning = false;
                canMove = true;
            }
            catch { }

        }
        private bool isOnGround(Player player){

            return player.MovementContext.IsGrounded;
        }
        private void setLessGravity(Player player, bool choice)
        {
            if (choice)
            {
                var localSpace = Camera.main.transform.InverseTransformDirection(gravityVector.Value);
                Physics.gravity = localSpace.normalized;
            }
            else
            {
                Physics.gravity = new Vector3(0f, -9.8f, 0f);
            }
        }
        private async Task<AudioClip> LoadAudioClip(string uri)
        {
            using (UnityWebRequest web = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS))
            {
                var asyncOperation = web.SendWebRequest();

                while (!asyncOperation.isDone)
                    await Task.Yield();

                if (!web.isNetworkError && !web.isHttpError)
                {
                    return DownloadHandlerAudioClip.GetContent(web);
                }
                else
                {
                    Debug.LogError($"Can't load audio at path: '{uri}', error: {web.error}");
                    return null;
                }
            }
        }

    }



}
