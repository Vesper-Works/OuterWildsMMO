﻿using OWML.ModHelper;
using OWML.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sfs2X;
using Sfs2X.Core;
using Sfs2X.Util;
using Sfs2X.Entities.Data;
using OWML.ModHelper.Events;
using Sfs2X.Requests;
using Sfs2X.Requests.MMO;
using UnityEngine.SceneManagement;
using UnityEngine;
using Sfs2X.Entities;
using Sfs2X.Entities.Variables;
using System.Collections;
using OWML.Common.Menus;

namespace ModTemplate
{
    public class ConnectionController : ModBehaviour
    {
        public static IModHelper ModHelperInstance;
        public static SmartFox Connection { get => Instance.sfs; }

        private SmartFox sfs;

        private Dictionary<int, GameObject> remotePlayers = new Dictionary<int, GameObject>();
        private Dictionary<int, GameObject> remoteShips = new Dictionary<int, GameObject>();

        private Sector closestSectorToPlayer;
        private int closestSectorToPlayerID;

        private Vector3 lastPlayerPos = Vector3.zero;

        private Vector3 lastShipPos = Vector3.zero;
        private Vector3 lastShipRot = Vector3.zero;

        private bool playerInGame = false;

        private PlayerCharacterController playerCharacterController;
        private JetpackThrusterModel playerThrusterModel;
        private ShipThrusterModel shipThrusterModel;

        private UnityEngine.UI.Text pingText;

        private IModButton connectButton;

        private string serverIP = "34.125.152.123";
        private static ConnectionController Instance { get; set; }
        public override void Configure(IModConfig config)
        {
            serverIP = config.GetSettingsValue<string>("serverIP");
        }
        void Start()
        {
            Instance = this;
            ModHelperInstance = ModHelper;
            //UnityExplorer.ExplorerStandalone.CreateInstance();

            Application.runInBackground = true;
            // Skip flash screen.
            var titleScreenAnimation = FindObjectOfType<TitleScreenAnimation>();
            titleScreenAnimation.SetValue("_fadeDuration", 0);
            titleScreenAnimation.SetValue("_gamepadSplash", false);
            titleScreenAnimation.SetValue("_introPan", false);
            titleScreenAnimation.Invoke("FadeInTitleLogo");

            // Skip menu fade.
            var titleAnimationController = FindObjectOfType<TitleAnimationController>();
            titleAnimationController.SetValue("_logoFadeDelay", 0.001f);
            titleAnimationController.SetValue("_logoFadeDuration", 0.001f);
            titleAnimationController.SetValue("_optionsFadeDelay", 0.001f);
            titleAnimationController.SetValue("_optionsFadeDuration", 0.001f);
            titleAnimationController.SetValue("_optionsFadeSpacing", 0.001f);

            ModHelper.Menus.MainMenu.OnInit += DoMainMenuStuff;

            ModHelper.Events.Scenes.OnCompleteSceneChange += OnCompleteSceneChange;

            GlobalMessenger<DeathType>.AddListener("PlayerDeath", new Callback<DeathType>(this.OnPlayerDeath));
        }

        private void OnPlayerDeath(DeathType deathType)
        {
            var data = new SFSObject();
            data.PutInt("died", (int)deathType); //JoinedGame
            sfs.Send(new ExtensionRequest("GeneralEvent", data, sfs.LastJoinedRoom));
        }

        private void DoMainMenuStuff()
        {
            connectButton = ModHelper.Menus.MainMenu.NewExpeditionButton.Duplicate("");
            connectButton.OnClick += StartUpConnection;
            SetButtonConnect();
        }
        private void OnCompleteSceneChange(OWScene oldScene, OWScene newScene)
        {
            if ((newScene == OWScene.SolarSystem || newScene == OWScene.EyeOfTheUniverse) && !playerInGame)
            {
                LoadServerThings();
                playerInGame = true;
            }
            else if ((newScene == OWScene.SolarSystem || newScene == OWScene.EyeOfTheUniverse) && playerInGame)
            {
                ReloadServerThings();
            }
        }
        void FixedUpdate()
        {
            if (sfs != null)
            {
                sfs.ProcessEvents();

                //On the server side the UserVariable event is captured and the coordinates are also passed to the MMOApi.SetUserPosition(...) method to update our position in the Room's map.
                //This in turn will keep us in sync with all the other players within our Area of Interest (AoI).

                //The SFSUserVariables are named after the abbreviation to keep network load low (apparently according to SFS docs)

                if (Locator.GetPlayerTransform() != null && closestSectorToPlayer != null)
                {
                    if (lastPlayerPos.ApproxEquals(Locator.GetPlayerTransform().position, 0.01f)) { return; }

                    lastPlayerPos = Locator.GetPlayerTransform().position;
                    Vector3 pos = closestSectorToPlayer.transform.InverseTransformPoint(Locator.GetPlayerTransform().position);
                    List<UserVariable> userVariables = new List<UserVariable>();
                    userVariables.Add(new SFSUserVariable("x", (double)pos.x));
                    userVariables.Add(new SFSUserVariable("y", (double)pos.y));
                    userVariables.Add(new SFSUserVariable("z", (double)pos.z));

                    Vector3 rot = closestSectorToPlayer.transform.InverseTransformRotation(Locator.GetPlayerTransform().rotation).eulerAngles;
                    userVariables.Add(new SFSUserVariable("rotx", (double)rot.x));
                    userVariables.Add(new SFSUserVariable("roty", (double)rot.y));
                    userVariables.Add(new SFSUserVariable("rotz", (double)rot.z));

                    userVariables.Add(new SFSUserVariable("sec", closestSectorToPlayerID));

                    sfs.Send(new SetUserVariablesRequest(userVariables));
                }
            }
        }
        IEnumerator SendPlayerData()
        {
            yield return new WaitForSeconds(2f);
            while (true)
            {
                var data = new SFSObject();

                //if (playerCharacterController.GetJumpCrouchFraction() != 0) TODO: send only when needed (JFC is not 0 and one after it is)
                {
                    data.PutFloat("jcf", playerCharacterController.GetJumpCrouchFraction());
                }

                Vector3 relativeGroundVelocity = playerCharacterController.GetRelativeGroundVelocity();
                data.PutFloat("rgvx", relativeGroundVelocity.x);
                data.PutFloat("rgvy", relativeGroundVelocity.y);
                data.PutFloat("rgvz", relativeGroundVelocity.z);

                if (playerThrusterModel.IsTranslationalThrusterFiring())
                {
                    data.PutFloat("tmla", playerThrusterModel.GetLocalAcceleration().y);
                }

                sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
                yield return new WaitForSeconds(0.1f);
            }
        }
        IEnumerator SendShipData()
        {
            yield return new WaitForSeconds(2f);
            SyncShipInstant();

            while (true)
            {
                //if (closestSectorToPlayer == null) { yield return new WaitForFixedUpdate(); }
               var data = new SFSObject();
               var pos = closestSectorToPlayer.transform.InverseTransformPoint(Locator.GetShipTransform().position);
                if (!lastShipPos.ApproxEquals(pos, 0.01f))
                {
                    lastShipPos = pos;
                    data.PutFloat("x", pos.x);
                    data.PutFloat("y", pos.y);
                    data.PutFloat("z", pos.z);
                }

                var rot = closestSectorToPlayer.transform.InverseTransformRotation(Locator.GetShipTransform().rotation).eulerAngles;
                if (!lastShipRot.ApproxEquals(rot, 0.01f))
                {
                    lastShipRot = rot;
                    data.PutFloat("rotx", rot.x);
                    data.PutFloat("roty", rot.y);
                    data.PutFloat("rotz", rot.z);
                }

                data.PutInt("sec", closestSectorToPlayerID);

                if (shipThrusterModel.IsTranslationalThrusterFiring())
                {
                    data.PutFloat("tmla", shipThrusterModel.GetLocalAcceleration().y);
                }

                sfs.Send(new ExtensionRequest("SyncShipData", data, sfs.LastJoinedRoom));
                yield return new WaitForFixedUpdate();
            }
        }

        private void SyncShipInstant()
        {
            var data = new SFSObject();

            Vector3 pos = closestSectorToPlayer.transform.InverseTransformPoint(Locator.GetShipTransform().position);
            lastShipPos = pos;
            data.PutFloat("x", pos.x);
            data.PutFloat("y", pos.y);
            data.PutFloat("z", pos.z);

            Vector3 rot = closestSectorToPlayer.transform.InverseTransformRotation(Locator.GetShipTransform().rotation).eulerAngles;
            lastShipRot = rot;
            data.PutFloat("rotx", rot.x);
            data.PutFloat("roty", rot.y);
            data.PutFloat("rotz", rot.z);

            data.PutInt("sec", closestSectorToPlayerID);
            sfs.Send(new ExtensionRequest("SyncShipData", data, sfs.LastJoinedRoom));
        }

        IEnumerator GetClosestSectorToPlayer()
        {
            yield return new WaitForSeconds(2f);
            while (true)
            {
                int closestSectorID = -1;
                Sector closestSector = null;
                float closestDistance = float.MaxValue;
                float currentDistance;

                foreach (var sector in SFSSectorManager.Sectors)
                {
                    if (!Locator.GetPlayerSectorDetector().IsWithinSector(sector.Value.GetName()) || sector.Value.GetName() == Sector.Name.Ship) { continue; }
                    currentDistance = Vector3.Distance(sector.Value.transform.position, Locator.GetPlayerTransform().position);
                    if (currentDistance < closestDistance)
                    {
                        closestDistance = currentDistance;
                        closestSector = sector.Value;
                        closestSectorID = sector.Key;
                    }
                }
                if (closestSector != null)
                {
                    closestSectorToPlayer = closestSector;
                    closestSectorToPlayerID = closestSectorID;
                }
                yield return new WaitForSeconds(0.4f);
            }
        }

        // For compatibility with NomaiVR. NomaiVR hides the player body, so we need to show it again.
        private static void MakeBodyPartsVisible(Transform bodyTransform)
        {
            var renderers = bodyTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer.gameObject.layer == LayerMask.NameToLayer("VisibleToProbe"))
                {
                    renderer.gameObject.layer = LayerMask.NameToLayer("Default");
                }
            }

            // NomaiVR also completely disables the player arms, so we gotta enable those.
            var withoutSuit = bodyTransform.Find("player_mesh_noSuit:Traveller_HEA_Player");
            withoutSuit.Find("player_mesh_noSuit:Player_LeftArm").gameObject.SetActive(true);
            withoutSuit.Find("player_mesh_noSuit:Player_RightArm").gameObject.SetActive(true);
            var withSuit = bodyTransform.Find("Traveller_Mesh_v01:Traveller_Geo");
            withSuit.Find("Traveller_Mesh_v01:PlayerSuit_LeftArm").gameObject.SetActive(true);
            withSuit.Find("Traveller_Mesh_v01:PlayerSuit_RightArm").gameObject.SetActive(true);
        }

        private void SpawnRemotePlayer(SFSUser user, Vector3 vector3, Quaternion quaternion)
        {
            if (remotePlayers.ContainsKey(user.Id))
            {
                ModHelper.Console.WriteLine("Player " + user + " is already spawned!"); return;
            }

            ModHelper.Console.WriteLine("Spawned: " + user);

            GameObject remotePlayer = new GameObject(user.Name);
            GameObject localPlayerBody = Locator.GetPlayerTransform().Find("Traveller_HEA_Player_v2").gameObject;
            GameObject remotePlayerBody = Instantiate(localPlayerBody);
            MakeBodyPartsVisible(remotePlayerBody.transform);

            remotePlayer.AddComponent<OWRigidbody>().MakeKinematic();
            remotePlayer.AddComponent<ImpactSensor>();
            remotePlayer.AddComponent<ForceDetector>();
            remotePlayer.AddComponent<FluidDetector>();

            remotePlayer.AddComponent<SimpleRemoteInterpolation>();
            Destroy(remotePlayerBody.GetComponent<PlayerAnimController>());

            remotePlayerBody.transform.SetParent(remotePlayer.transform);
            remotePlayerBody.transform.localPosition = new Vector3(0f, -1.03f, -0.2f);
            remotePlayerBody.transform.localRotation = Quaternion.Euler(-1.500009f, 0f, 0f);
            remotePlayerBody.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);

            remotePlayerBody.transform.Find("player_mesh_noSuit:Traveller_HEA_Player/player_mesh_noSuit:Player_Head").gameObject.layer = 0;
            remotePlayerBody.transform.Find("Traveller_Mesh_v01:Traveller_Geo/Traveller_Mesh_v01:PlayerSuit_Helmet").gameObject.layer = 0;

            remotePlayerBody.AddComponent<PlayerAnimationSync>();
            remotePlayer.AddComponent<PlayerControllerSync>();

            GameObject remoteVFXObjects = new GameObject("RemotePlayerVFX");
            remoteVFXObjects.transform.SetParent(remotePlayer.transform);
            remoteVFXObjects.AddComponent<PlayerParticlesControllerSync>();

            GameObject thrusterWash = new GameObject("ThrusterWash");
            thrusterWash.transform.SetParent(remoteVFXObjects.transform);
            Instantiate(Locator.GetPlayerTransform().Find("PlayerVFX/ThrusterWash/ThrusterWash_Default"), thrusterWash.transform);
            thrusterWash.AddComponent<ThrusterWashControllerSync>();

            Locator.GetPlayerTransform().Find("PlayerVFX/Thrusters").gameObject.SetActive(false);
            GameObject thrusters = Instantiate(Locator.GetPlayerTransform().Find("PlayerVFX/Thrusters").gameObject, remoteVFXObjects.transform);
            Locator.GetPlayerTransform().Find("PlayerVFX/Thrusters").gameObject.SetActive(true);
            ReplaceThrusterFlameControllerRecursively(thrusters.transform);
            //thrusters.SetActive(true);
            remotePlayer.AddComponent<PlayerStateSync>();

            var obj = GameObject.FindWithTag("MapCamera");
            var markerManager = obj.GetRequiredComponent<MapController>().GetMarkerManager();
            var canvasMarker = markerManager.InstantiateNewMarker(true);
            markerManager.RegisterMarker(canvasMarker, remotePlayer.GetComponent<OWRigidbody>());
            canvasMarker.SetLabel(user.Name.ToUpper());
            canvasMarker.SetColor(Color.white);
            canvasMarker.SetVisibility(true);

            remotePlayer.AddComponent<RemotePlayerHUDMarker>().InitCanvasMarker(user.Name);
            //remotePlayer.AddComponent<LockOnReticule>().Init();


            remotePlayer.transform.position = vector3;
            remotePlayer.transform.rotation = quaternion;
            remotePlayers.Add(user.Id, remotePlayer);
            SpawnRemoteShip(user);

        }
        private void RemoveRemotePlayer(SFSUser user)
        {
            ModHelper.Console.WriteLine("Removed: " + user);
            Destroy(remotePlayers[user.Id]);
            Destroy(remoteShips[user.Id]);
            remotePlayers.Remove(user.Id);
            remoteShips.Remove(user.Id);
        }
 
        private void SpawnRemoteShip(SFSUser user)
        {
            GameObject remotePlayerShip = new GameObject(user.Name + "'s ship");

            remotePlayerShip.AddComponent<ProxyShadowCasterSuperGroup>();
            remotePlayerShip.AddComponent<SimpleRemoteInterpolation>();
            remotePlayerShip.AddComponent<OWRigidbody>().MakeKinematic();
            remotePlayerShip.AddComponent<FluidDetector>();

            GameObject remoteVFXObjects = new GameObject("RemoteShipVFX");
            remoteVFXObjects.transform.SetParent(remotePlayerShip.transform);

            Instantiate(GameObject.Find("Ship_Body/Module_Cabin/Effects_Cabin/ThrusterWash/ThrusterWash_Ship"), remoteVFXObjects.transform);
            //Instantiate(GameObject.Find("Ship_Body/Module_Supplies/Effects_Supplies/ThrusterWash_Supplies"), remoteVFXObjects.transform);

            GameObject.Find("Ship_Body/Module_Engine/Effects_Engine/Thrusters").gameObject.SetActive(false);
            GameObject.Find("Ship_Body/Module_Supplies/Effects_Supplies/Thrusters").gameObject.SetActive(false);
            GameObject thrusters1 = Instantiate(GameObject.Find("Ship_Body/Module_Engine/Effects_Engine/Thrusters"), remoteVFXObjects.transform);
            GameObject thrusters2 = Instantiate(GameObject.Find("Ship_Body/Module_Supplies/Effects_Supplies/Thrusters"), remoteVFXObjects.transform);
            GameObject.Find("Ship_Body/Module_Engine/Effects_Engine/Thrusters").gameObject.SetActive(true);
            GameObject.Find("Ship_Body/Module_Supplies/Effects_Supplies/Thrusters").gameObject.SetActive(true);
            ReplaceThrusterFlameControllerRecursively(remoteVFXObjects.transform);


            Instantiate(GameObject.Find("Ship_Body/Module_Cabin/Geo_Cabin/Cabin_Geometry/Cabin_Exterior"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_Cabin/Geo_Cabin/Cabin_Tech/Cabin_Tech_Exterior"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_Cabin/Geo_Cabin/Shadowcaster_Cabin"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);

            Instantiate(GameObject.Find("Ship_Body/Module_Cockpit/Geo_Cockpit/Cockpit_Geometry/Cockpit_Exterior"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_Cockpit/Geo_Cockpit/Cockpit_Tech/Cockpit_Tech_Exterior"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_Cockpit/Geo_Cockpit/Cockpit_Geometry/ShadowCaster_Cockpit"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);

            Instantiate(GameObject.Find("Ship_Body/Module_Engine/Geo_Engine/Engine_Geometry/Engine_Exterior"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_Engine/Geo_Engine/ShadowCaster_Engine"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);

            Instantiate(GameObject.Find("Ship_Body/Module_Supplies/Geo_Supplies/Supplies_Geometry/Supplies_Exterior"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_Supplies/Geo_Supplies/ShadowCaster_Supplies"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);

            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Front/Geo_LandingGear_Front/LandingGear_FrontFoot"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Front/Geo_LandingGear_Front/LandingGear_FrontLeg"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Front/Geo_LandingGear_Front/ShadowCaster_FrontFoot"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Front/Geo_LandingGear_Front/ShadowCaster_FrontLeg"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Front/LandingGear_Front_Tech"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);

            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Left/Geo_LandingGear_Left/LandingGear_LeftFoot"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Left/Geo_LandingGear_Left/LandingGear_LeftLeg"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Left/Geo_LandingGear_Left/ShadowCaster_LeftFoot"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Left/Geo_LandingGear_Left/ShadowCaster_LeftLeg"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);

            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Right/Geo_LandingGear_Right/LandingGear_RightFoot"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Right/Geo_LandingGear_Right/LandingGear_RightLeg"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Right/Geo_LandingGear_Right/ShadowCaster_RightFoot"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);
            Instantiate(GameObject.Find("Ship_Body/Module_LandingGear/LandingGear_Right/Geo_LandingGear_Right/ShadowCaster_RightLeg"), remotePlayerShip.transform).transform.localPosition -= new Vector3(0, 4, 0);

            RemoveCollisionFromObjectRecursively(remotePlayerShip.transform);
            remotePlayerShip.AddComponent<ThrusterWashControllerSync>();

            thrusters1.SetActive(true);
            thrusters2.SetActive(true);

            remoteShips.Add(user.Id, remotePlayerShip);

            SyncShipInstant();
        }
        private void RemoveCollisionFromObjectRecursively(Transform transform)
        {
            foreach (Transform child in transform)
            {
                if (child.TryGetComponent<Collider>(out _) || child.TryGetComponent<Rigidbody>(out _) || child.TryGetComponent<OWRigidbody>(out _))
                {
                    Destroy(child.gameObject);
                }
                if (child.childCount > 0)
                {
                    RemoveCollisionFromObjectRecursively(child);
                }
            }
        }

        private void ReplaceThrusterFlameControllerRecursively(Transform transform)
        {
            ThrusterFlameController thrusterFlameController;
            ThrusterParticlesBehavior thrusterParticlesBehavior;
            foreach (Transform child in transform)
            {
                if (child.TryGetComponent<ThrusterFlameController>(out thrusterFlameController))
                {
                    Destroy(thrusterFlameController);
                    child.gameObject.AddComponent<ThrusterFlameControllerSync>();
                }
                if (child.TryGetComponent<ThrusterParticlesBehavior>(out thrusterParticlesBehavior))
                {
                    Destroy(thrusterParticlesBehavior);
                }
                if (child.childCount > 0)
                {
                    ReplaceThrusterFlameControllerRecursively(child);
                }
            }
        }

        //----------------------------------------------------------
        // SmartFoxServer event listeners
        //----------------------------------------------------------

        /**
         * This is where we receive events about people in proximity (AoI).
         * We get two lists, one of new users that have entered the AoI and one with users that have left our proximity area.
         */
        public void OnProximityListUpdate(BaseEvent evt)
        {

            var addedUsers = (List<User>)evt.Params["addedUsers"];
            var removedUsers = (List<User>)evt.Params["removedUsers"];

            // Handle all new Users
            foreach (User user in addedUsers)
            {
                SpawnRemotePlayer(
                    (SFSUser)user,
                    new Vector3((float)user.GetVariable("x").GetDoubleValue(), (float)user.GetVariable("y").GetDoubleValue(), (float)user.GetVariable("z").GetDoubleValue()),
                    Quaternion.Euler((float)user.GetVariable("rotx").GetDoubleValue(), (float)user.GetVariable("roty").GetDoubleValue(), (float)user.GetVariable("rotz").GetDoubleValue())
                );
            }

            // Handle removed users
            foreach (User user in removedUsers)
            {
                RemoveRemotePlayer((SFSUser)user);
            }
        }

        /**
         * When user variable is updated on any client within the AoI, then this event is received.
         * This is where most of the game logic for this example is contained.
         */
        public void OnUserVariableUpdate(BaseEvent evt)
        {

            List<string> changedVars = (List<string>)evt.Params["changedVars"];

            SFSUser user = (SFSUser)evt.Params["user"];
            if (user == sfs.MySelf) return;

            if (!remotePlayers.ContainsKey(user.Id) ||
                remotePlayers[user.Id] == null)
            {
                ModHelper.Console.WriteLine("Ship trying to sync not found!");
                return;
            }

            if (changedVars.Contains("x"))
            {
                remotePlayers[user.Id].GetComponent<SimpleRemoteInterpolation>().SetPosition(
                    new Vector3((float)user.GetVariable("x").GetDoubleValue(), (float)user.GetVariable("y").GetDoubleValue(), (float)user.GetVariable("z").GetDoubleValue()),
                    true,
                    user.GetVariable("sec").GetIntValue()
                );
            }
            if (changedVars.Contains("rotx"))
            {
                remotePlayers[user.Id].GetComponent<SimpleRemoteInterpolation>().SetRotation(
                    Quaternion.Euler(new Vector3((float)user.GetVariable("rotx").GetDoubleValue(), (float)user.GetVariable("roty").GetDoubleValue(), (float)user.GetVariable("rotz").GetDoubleValue())),
                    true,
                    user.GetVariable("sec").GetIntValue()
                );
            }

        }

        private void LoadServerThings()
        {
            ModHelper.Console.WriteLine("Loaded game scene!");

            sfs.EnableLagMonitor(true, 2, 5);

            // Register callback delegates
            sfs.AddEventListener(SFSEvent.CONNECTION_LOST, OnConnectionLost);
            sfs.AddEventListener(SFSEvent.USER_VARIABLES_UPDATE, OnUserVariableUpdate);
            sfs.AddEventListener(SFSEvent.PROXIMITY_LIST_UPDATE, OnProximityListUpdate);
            sfs.AddEventListener(SFSEvent.EXTENSION_RESPONSE, OnExtensionResponse);
            sfs.AddEventListener(SFSEvent.PING_PONG, PingPongHandler);

            SFSSectorManager.RefreshSectors();


            StartCoroutine(GetClosestSectorToPlayer());
            StartCoroutine(SendPlayerData());
            StartCoroutine(SendShipData());
            StartCoroutine(SendJoinedGameMessage());
            playerThrusterModel = FindObjectOfType<JetpackThrusterModel>();
            shipThrusterModel = FindObjectOfType<ShipThrusterModel>();

            SortOutListeners();

            gameObject.AddComponent<ChatHandler>();

            //ModHelper.HarmonyHelper.AddPostfix<PauseMenuManager>("OnExitToMainMenu", typeof(ConnectionController), "OnExitToMainMenuPatch");

        }
        private void ReloadServerThings()
        {
            ModHelper.Console.WriteLine("Reloaded game scene!");

            sfs.EnableLagMonitor(true, 2, 5);

            sfs.RemoveAllEventListeners();

            // Register callback delegates
            sfs.AddEventListener(SFSEvent.CONNECTION_LOST, OnConnectionLost);
            sfs.AddEventListener(SFSEvent.USER_VARIABLES_UPDATE, OnUserVariableUpdate);
            sfs.AddEventListener(SFSEvent.PROXIMITY_LIST_UPDATE, OnProximityListUpdate);
            sfs.AddEventListener(SFSEvent.EXTENSION_RESPONSE, OnExtensionResponse);
            sfs.AddEventListener(SFSEvent.PING_PONG, PingPongHandler);

            SFSSectorManager.RefreshSectors();

            playerThrusterModel = FindObjectOfType<JetpackThrusterModel>();
            shipThrusterModel = FindObjectOfType<ShipThrusterModel>();

            SortOutListeners();

            //gameObject.AddComponent<ChatHandler>();

            StopAllCoroutines();
            StartCoroutine(GetClosestSectorToPlayer());
            StartCoroutine(SendPlayerData());
            StartCoroutine(SendShipData());
            StartCoroutine(SendJoinedGameMessage());
            StartCoroutine(ReloadAllRemoteUsers(0f));

            //ModHelper.HarmonyHelper.AddPostfix<PauseMenuManager>("OnExitToMainMenu", typeof(ConnectionController), "OnExitToMainMenuPatch");

        }

        private IEnumerator ReloadAllRemoteUsers(float delay)
        {
            yield return new WaitForSeconds(delay);
            List<int> userIDs = new List<int>();
            foreach (var userID in remotePlayers.Keys)
            {
                userIDs.Add(userID);
            }
            foreach (var userID in userIDs)
            {

                RemoveRemotePlayer((SFSUser)sfs.UserManager.GetUserById(userID));
                SpawnRemotePlayer((SFSUser)sfs.UserManager.GetUserById(userID), Vector3.zero, Quaternion.identity);
            }
        }
        private void OnExitToMainMenuPatch()
        {
            StartCoroutine(SendLeftGameMessage());
        }

        private IEnumerator SendJoinedGameMessage()
        {
            yield return new WaitForSeconds(3f);
            var data = new SFSObject();
            data.PutNull("jg"); //JoinedGame
            sfs.Send(new ExtensionRequest("GeneralEvent", data, sfs.LastJoinedRoom));
        }

        private IEnumerator SendLeftGameMessage()
        {
            yield return new WaitForSeconds(3f);
            var data = new SFSObject();
            data.PutNull("lg"); //LeftGame
            sfs.Send(new ExtensionRequest("GeneralEvent", data, sfs.LastJoinedRoom));
        }
        private void PingPongHandler(BaseEvent evt)
        {
            if (pingText != null) { pingText.text = (int)evt.Params["lagValue"] + "ms"; }
        }

        private void SortOutListeners()
        {
            playerCharacterController = FindObjectOfType<PlayerCharacterController>();
            playerCharacterController.OnJump += PlayerJump;
            playerCharacterController.OnBecomeGrounded += PlayerGrounded;
            playerCharacterController.OnBecomeUngrounded += PlayerUngrounded;

            playerThrusterModel.OnStartTranslationalThrust += PlayerStartedTranslationalThrust;
            playerThrusterModel.OnStopTranslationalThrust += PlayerStoppedTranslationalThrust;

            shipThrusterModel.OnStartTranslationalThrust += ShipStartedTranslationalThrust;
            shipThrusterModel.OnStopTranslationalThrust += ShipStoppedTranslationalThrust;

            //GlobalMessenger.AddListener("EnterConversation", new Callback(this.OnEnterConversation));
            //GlobalMessenger.AddListener("ExitConversation", new Callback(this.OnExitConversation));
            //GlobalMessenger.AddListener("EnterDarkZone", new Callback(this.OnEnterDarkZone));
            //GlobalMessenger.AddListener("ExitDarkZone", new Callback(this.OnExitDarkZone));
            //GlobalMessenger.AddListener("TurnOnFlashlight", new Callback(PlayerStateSync.OnFlashlightOn));
            //GlobalMessenger.AddListener("TurnOffFlashlight", new Callback(PlayerStateSync.OnFlashlightOff));
            GlobalMessenger.AddListener("InitPlayerForceAlignment", new Callback(PlayerInitPlayerForceAlignment));
            GlobalMessenger.AddListener("BreakPlayerForceAlignment", new Callback(PlayerBreakPlayerForceAlignment));
            //GlobalMessenger<Signalscope>.AddListener("EnterSignalscopeZoom", new Callback<Signalscope>(PlayerStateSync.OnEnterSignalscopeZoom));
            //GlobalMessenger.AddListener("ExitSignalscopeZoom", new Callback(PlayerStateSync.OnExitSignalscopeZoom));
            //GlobalMessenger.AddListener("EnterShipComputer", new Callback(PlayerStateSync.OnEnterShipComputer));
            //GlobalMessenger.AddListener("ExitShipComputer", new Callback(PlayerStateSync.OnExitShipComputer));
            //GlobalMessenger.AddListener("EnterLandingView", new Callback(PlayerStateSync.OnEnterLandingView));
            //GlobalMessenger.AddListener("ExitLandingView", new Callback(PlayerStateSync.OnExitLandingView));
            //GlobalMessenger.AddListener("EnterShip", new Callback(PlayerStateSync.OnEnterShip));
            //GlobalMessenger.AddListener("ExitShip", new Callback(PlayerStateSync.OnExitShip));
            GlobalMessenger.AddListener("SuitUp", new Callback(PlayerSuitUp));
            GlobalMessenger.AddListener("RemoveSuit", new Callback(PlayerRemoveSuit));
            //GlobalMessenger.AddListener("EnterShuttle", new Callback(PlayerStateSync.OnEnterShuttle));
            //GlobalMessenger.AddListener("ExitShuttle", new Callback(PlayerStateSync.OnExitShuttle));
            //GlobalMessenger.AddListener("EnterMapView", new Callback(PlayerStateSync.OnEnterMapView));
            //GlobalMessenger.AddListener("ExitMapView", new Callback(PlayerStateSync.OnExitMapView));
            //GlobalMessenger<OWRigidbody>.AddListener("EnterFlightConsole", new Callback<OWRigidbody>(PlayerStateSync.OnEnterFlightConsole));
            //GlobalMessenger.AddListener("ExitFlightConsole", new Callback(PlayerStateSync.OnExitFlightConsole));
            //GlobalMessenger<float>.AddListener("PlayerCameraEnterWater", new Callback<float>(PlayerStateSync.OnCameraEnterWater));
            //GlobalMessenger.AddListener("PlayerCameraExitWater", new Callback(PlayerStateSync.OnCameraExitWater));
            //GlobalMessenger<OWRigidbody>.AddListener("AttachPlayerToPoint", new Callback<OWRigidbody>(this.OnAttachPlayerToPoint));
            //GlobalMessenger.AddListener("DetachPlayerFromPoint", new Callback(this.OnDetachPlayerFromPoint));
            //GlobalMessenger.AddListener("PlayerEnterBrambleDimension", new Callback(PlayerStateSync.OnPlayerEnterBrambleDimension));
            //GlobalMessenger.AddListener("PlayerExitBrambleDimension", new Callback(PlayerStateSync.OnPlayerExitBrambleDimension));
            //GlobalMessenger.AddListener("PlayerEnterGiantsDeep", new Callback(PlayerStateSync.OnPlayerEnterGiantsDeep));
            //GlobalMessenger.AddListener("PlayerExitGiantsDeep", new Callback(PlayerStateSync.OnPlayerExitGiantsDeep));
            //GlobalMessenger.AddListener("EnterZeroGTraining", new Callback(this.OnEnterZeroGTraining));
            //GlobalMessenger.AddListener("ExitZeroGTraining", new Callback(this.OnExitZeroGTraining));
            //GlobalMessenger.AddListener("PlayerCameraLockOn", new Callback(this.OnPlayerCameraLockOn));
            //GlobalMessenger.AddListener("PlayerCameraBreakLock", new Callback(this.OnPlayerCameraBreakLock));
            //GlobalMessenger<DeathType>.AddListener("PlayerDeath", new Callback<DeathType>(PlayerStateSync.OnPlayerDeath));
            //GlobalMessenger.AddListener("PlayerResurrection", new Callback(PlayerStateSync.OnPlayerResurrection));
            //GlobalMessenger.AddListener("ShipHullBreach", new Callback(PlayerStateSync.OnShipHullBreach));
            //GlobalMessenger.AddListener("EnterNomaiRemoteCamera", new Callback(this.OnEnterNomaiRemoteCamera));
            //GlobalMessenger.AddListener("ExitNomaiRemoteCamera", new Callback(this.OnExitNomaiRemoteCamera));
            //GlobalMessenger<EyeState>.AddListener("EyeStateChanged", new Callback<EyeState>(this.OnEyeStateChanged));
            //GlobalMessenger<bool>.AddListener("StartSleepingAtCampfire", new Callback<bool>(this.OnStartSleepingAtCampfire));
            //GlobalMessenger.AddListener("StopSleepingAtCampfire", new Callback(this.OnStopSleepingAtCampfire));
            //GlobalMessenger.AddListener("StartFastForward", new Callback(this.OnStartFastForward));
            //GlobalMessenger.AddListener("EndFastForward", new Callback(this.OnEndFastForward));
            //GlobalMessenger.AddListener("EnterCloak", new Callback(this.OnEnterCloak));
            //GlobalMessenger.AddListener("ExitCloak", new Callback(this.OnExitCloak));
            //GlobalMessenger.AddListener("EnterDreamWorld", new Callback(this.OnEnterDreamWorld));
            //GlobalMessenger.AddListener("ExitDreamWorld", new Callback(this.OnExitDreamWorld));
            //GlobalMessenger.AddListener("StartViewingProjector", new Callback(this.OnStartViewingProjector));
            //GlobalMessenger.AddListener("EndViewingProjector", new Callback(this.OnEndViewingProjector));
            //GlobalMessenger<Peephole>.AddListener("StartPeeping", new Callback<Peephole>(this.OnStartPeeping));
            //GlobalMessenger<Peephole>.AddListener("StopPeeping", new Callback<Peephole>(this.OnStopPeeping));
            //GlobalMessenger.AddListener("PlayerEnterUndertowVolume", new Callback(this.OnEnterUndertowVolume));
            //GlobalMessenger.AddListener("PlayerExitUndertowVolume", new Callback(this.OnExitUndertowVolume));
            //GlobalMessenger.AddListener("PlayerGrabbedByGhost", new Callback(this.OnGrabbedByGhost));
            //GlobalMessenger.AddListener("PlayerReleasedByGhost", new Callback(this.OnReleasedByGhost));
        }

        private void PlayerJump()
        {
            var data = new SFSObject();
            data.PutNull("jump");
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerGrounded()
        {
            var data = new SFSObject();
            data.PutNull("g");//Grounded
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerUngrounded()
        {
            var data = new SFSObject();
            data.PutNull("ug");//Ungrounded
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerSuitUp()
        {
            if (GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/Ping") == null)
            {
                var pingTextObject = Instantiate(GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/NumericalReadout"), GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce").transform);
                foreach (Transform child in pingTextObject.transform)
                {
                    if (child.gameObject.name.ToLower().Contains("clone")) { Destroy(child.gameObject); }
                }
                pingTextObject.name = "Ping";
                Destroy(pingTextObject.GetComponentInChildren<LocalizedText>());
                pingTextObject.transform.GetChild(0).GetComponent<UnityEngine.UI.Text>().text = "PING";
                pingTextObject.transform.position += new Vector3(0, 0.07f, 0);
                pingText = pingTextObject.GetComponent<UnityEngine.UI.Text>();
            }


            var data = new SFSObject();
            data.PutBool("suit", true);
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerRemoveSuit()
        {
            var data = new SFSObject();
            data.PutBool("suit", false);
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerInitPlayerForceAlignment()
        {
            var data = new SFSObject();
            data.PutBool("pfa", true);
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerBreakPlayerForceAlignment()
        {
            var data = new SFSObject();
            data.PutBool("pfa", false);
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerStartedTranslationalThrust()
        {
            var data = new SFSObject();
            data.PutBool("tt", true);
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void PlayerStoppedTranslationalThrust()
        {
            var data = new SFSObject();
            data.PutBool("tt", false);
            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
        }
        private void ShipStartedTranslationalThrust()
        {
            var data = new SFSObject();
            data.PutBool("tt", true);
            sfs.Send(new ExtensionRequest("SyncShipData", data, sfs.LastJoinedRoom));
        }
        private void ShipStoppedTranslationalThrust()
        {
            var data = new SFSObject();
            data.PutBool("tt", false);
            sfs.Send(new ExtensionRequest("SyncShipData", data, sfs.LastJoinedRoom));
        }

        private void SetButtonConnecting()
        {
            if (connectButton == null) return;
            connectButton.Button.enabled = false;
            connectButton.Title = "CONNECTING...";
        }
        
        private void SetButtonConnected()
        {
            if (connectButton == null) return;
            connectButton.Button.enabled = false;
            connectButton.Title = "CONNECTED";
        }

        private void SetButtonConnect()
        {
            if (connectButton == null) return;
            connectButton.Button.enabled = true;
            connectButton.Title = "CONNECT TO SERVER";
        }

        private void SetButtonError()
        {
            if (connectButton == null) return;
            connectButton.Button.enabled = true;
            connectButton.Title = "FAILED. TRY AGAIN?";
        }

        private void StartUpConnection()
        {
            SetButtonConnecting();

            // Create SmartFox client instance
            sfs = new SmartFox();
            
            // Add event listeners
            sfs.AddEventListener(SFSEvent.CONNECTION, OnConnection);
            sfs.AddEventListener(SFSEvent.CONNECTION_LOST, OnConnectionLost);
            sfs.AddEventListener(SFSEvent.LOGIN, OnLogin);
            sfs.AddEventListener(SFSEvent.LOGIN_ERROR, OnLoginError);
            sfs.AddEventListener(SFSEvent.EXTENSION_RESPONSE, OnExtensionResponse);
            sfs.AddEventListener(SFSEvent.ROOM_JOIN, OnRoomJoin);
            sfs.AddEventListener(SFSEvent.ROOM_JOIN_ERROR, OnRoomJoinError);

            // Set connection parameters
            ConfigData cfg = new ConfigData();
            cfg.Host = serverIP;
            cfg.Port = 9933;
            cfg.Zone = "OuterWildsMMO";

            // Connect to SFS2X
            sfs.Connect(cfg);
        }

        private void OnConnection(BaseEvent evt)
        {
            if ((bool)evt.Params["success"])
            {
                ModHelper.Console.WriteLine("Connected");

                // Send login request

#if DEBUG
                sfs.Send(new LoginRequest(""));
#else
                sfs.Send(new LoginRequest(StandaloneProfileManager.SharedInstance.currentProfile.profileName));
#endif
            }
            else
            {
                SetButtonError();
                ModHelper.Console.WriteLine("Connection failed", MessageType.Error);
            }
        }

        private void OnConnectionLost(BaseEvent evt)
        {
            SetButtonConnect();
            sfs.RemoveAllEventListeners();
            ModHelper.Console.WriteLine("Disconnected");
        }

        private void OnLogin(BaseEvent evt)
        {
            string roomName = "UnityMMODemo";

            // We either create the Game Room or join it if it exists already
            if (sfs.RoomManager.ContainsRoom(roomName))
            {
                sfs.Send(new JoinRoomRequest(roomName));
            }
            else
            {
                MMORoomSettings settings = new MMORoomSettings(roomName);
                settings.DefaultAOI = new Vec3D(10000000f, 10000000f, 10000000f);
                //settings.MapLimits = new MapLimits(new Vec3D(-100f, 1f, -100f), new Vec3D(100f, 1f, 100f));
                settings.MaxUsers = 100;
                settings.Extension = new RoomExtension("OuterWildsMMO", "MainExtension");
                settings.IsGame = true;
                settings.SendAOIEntryPoint = false;
                sfs.Send(new CreateRoomRequest(settings, true));
            }
            
            SetButtonConnected();
        }

        private void OnLoginError(BaseEvent evt)
        {
            ModHelper.Console.WriteLine("Login error: " + (string)evt.Params["errorMessage"], MessageType.Error);
            SetButtonError();
        }

        private void OnExtensionResponse(BaseEvent evt)
        {
            string cmd = (string)evt.Params["cmd"];

            if (evt.Params == null) { return; }
            if (evt.Params.Count == 0) { return; }
            if (!evt.Params.ContainsKey("params")) { return; }

            SFSObject responseParams = (SFSObject)evt.Params["params"];

            if (responseParams == null) { return; }

            if (!responseParams.ContainsKey("userId")) { return; }
            if (remotePlayers == null) { return; }
            GameObject remotePlayer = null;
            try
            {
                remotePlayer = remotePlayers[responseParams.GetInt("userId")];
            }
            catch (Exception)
            {
                ModHelper.Console.WriteLine("No remote player found!", MessageType.Error);
                return;
            }
            if (remotePlayer == null) { return; }

            switch (cmd)
            {

                case "SyncPlayerData":
                    #region SyncPlayerData
                    if (responseParams.ContainsKey("jcf"))
                    {
                        remotePlayer.GetComponent<PlayerControllerSync>().SetJumpCrouchFraction(responseParams.GetFloat("jcf"));
                    }
                    if (responseParams.ContainsKey("rgvx"))
                    {
                        remotePlayer.GetComponent<PlayerControllerSync>().SetRelativeGroundVelocity(
                            new Vector3(responseParams.GetFloat("rgvx"),
                                        responseParams.GetFloat("rgvy"),
                                        responseParams.GetFloat("rgvz")));
                    }
                    if (responseParams.ContainsKey("suit"))
                    {
                        if (responseParams.GetBool("suit") == true)
                        {
                            remotePlayer.GetComponentInChildren<PlayerAnimationSync>().OnPutOnSuit();
                            remotePlayer.GetComponent<PlayerStateSync>().OnSuitUp();
                        }
                        else
                        {
                            remotePlayer.GetComponentInChildren<PlayerAnimationSync>().OnRemoveSuit();
                            remotePlayer.GetComponent<PlayerStateSync>().OnRemoveSuit();
                        }
                    }
                    if (responseParams.ContainsKey("pfa"))
                    {
                        if (responseParams.GetBool("pfa") == true)
                        {
                            remotePlayer.GetComponent<PlayerStateSync>().OnInitPlayerForceAlignment();
                        }
                        else
                        {
                            remotePlayer.GetComponent<PlayerStateSync>().OnBreakPlayerForceAlignment();
                        }
                    }
                    if (responseParams.ContainsKey("tt"))
                    {
                        if (responseParams.GetBool("tt") == true)
                        {
                            remotePlayer.GetComponentInChildren<ThrusterWashControllerSync>().OnStartTranslationalThrust();
                            foreach (var thrusterController in remotePlayer.GetComponentsInChildren<ThrusterFlameControllerSync>())
                            {
                                thrusterController.OnStartTranslationalThrust();
                            }
                        }
                        else
                        {
                            remotePlayer.GetComponentInChildren<ThrusterWashControllerSync>().OnStopTranslationalThrust();
                            foreach (var thrusterController in remotePlayer.GetComponentsInChildren<ThrusterFlameControllerSync>())
                            {
                                thrusterController.OnStopTranslationalThrust();
                            }
                        }
                    }
                    if (responseParams.ContainsKey("tmla"))
                    {
                        remotePlayer.GetComponentInChildren<ThrusterWashControllerSync>().ThrusterModelLocalYAcceleration = responseParams.GetFloat("tmla");
                        remotePlayer.GetComponentInChildren<PlayerAnimationSync>().ThrusterModelLocalYAcceleration = responseParams.GetFloat("tmla");
                    }
                    if (responseParams.ContainsKey("j"))
                    {
                        remotePlayer.GetComponentInChildren<PlayerAnimationSync>().OnPlayerJump();
                    }
                    if (responseParams.ContainsKey("g"))
                    {
                        remotePlayer.GetComponentInChildren<PlayerAnimationSync>().OnPlayerGrounded();
                    }
                    if (responseParams.ContainsKey("ug"))
                    {
                        remotePlayer.GetComponentInChildren<PlayerAnimationSync>().OnPlayerUngrounded();
                    }

                    #endregion
                    break;

                case "SyncShipData":
                    #region SyncShipData

                    if (!remoteShips.ContainsKey(responseParams.GetInt("userId")) ||
                        remoteShips[responseParams.GetInt("userId")] == null)
                    {
                        ModHelper.Console.WriteLine("Ship trying to sync not found!");
                        return;
                    }
                    GameObject remoteShip = remoteShips[responseParams.GetInt("userId")];

                    if (responseParams.ContainsKey("x"))
                    {
                        remoteShip.GetComponent<SimpleRemoteInterpolation>().SetPosition(
                        new Vector3(responseParams.GetFloat("x"), responseParams.GetFloat("y"), responseParams.GetFloat("z")),
                        true,
                        responseParams.GetInt("sec"));
                    }
                    if (responseParams.ContainsKey("rotx"))
                    {
                        remoteShip.GetComponent<SimpleRemoteInterpolation>().SetRotation(
                        Quaternion.Euler(responseParams.GetFloat("rotx"), responseParams.GetFloat("roty"), responseParams.GetFloat("rotz")),
                        true,
                        responseParams.GetInt("sec"));
                    }

                    if (responseParams.ContainsKey("tmla"))
                    {
                        remoteShip.GetComponent<ThrusterWashControllerSync>().ThrusterModelLocalYAcceleration = responseParams.GetFloat("tmla");
                    }
                    if (responseParams.ContainsKey("tt"))
                    {
                        if (responseParams.GetBool("tt") == true)
                        {
                            remoteShip.GetComponent<ThrusterWashControllerSync>().OnStartTranslationalThrust();
                            foreach (var thrusterController in remoteShip.GetComponentsInChildren<ThrusterFlameControllerSync>())
                            {
                                thrusterController.OnStartTranslationalThrust();
                            }
                        }
                        else
                        {
                            remoteShip.GetComponent<ThrusterWashControllerSync>().OnStopTranslationalThrust();
                            foreach (var thrusterController in remoteShip.GetComponentsInChildren<ThrusterFlameControllerSync>())
                            {
                                thrusterController.OnStopTranslationalThrust();
                            }
                        }
                    }
                    #endregion
                    break;

                case "GeneralEvent":
                    #region GeneralEvent
                    if (responseParams.ContainsKey("jg"))
                    {
                        if (PlayerState.AtFlightConsole())
                        {
                            var data = new NotificationData(NotificationTarget.Ship, "Hearthian joined: " + remotePlayer.name, 4f, true);
                            NotificationManager.SharedInstance.PostNotification(data, false);
                        }
                        else
                        {
                            var data = new NotificationData(NotificationTarget.Player, "Hearthian joined: " + remotePlayer.name, 4f, true);
                            NotificationManager.SharedInstance.PostNotification(data, false);
                        }
                        if (PlayerState.IsWearingSuit())
                        {
                            var data = new SFSObject();
                            data.PutBool("suit", true);
                            sfs.Send(new ExtensionRequest("SyncPlayerData", data, sfs.LastJoinedRoom));
                        }
                        SyncShipInstant();
                    }
                    if (responseParams.ContainsKey("lg"))
                    {
                        if (PlayerState.AtFlightConsole())
                        {
                            var data = new NotificationData(NotificationTarget.Ship, "Hearthian left: " + remotePlayer.name, 4f, true);
                            NotificationManager.SharedInstance.PostNotification(data, false);
                        }
                        else
                        {
                            var data = new NotificationData(NotificationTarget.Player, "Hearthian left: " + remotePlayer.name, 4f, true);
                            NotificationManager.SharedInstance.PostNotification(data, false);
                        }
                    }
                    if (responseParams.ContainsKey("died"))
                    {
                        if (PlayerState.AtFlightConsole())
                        {
                            var data = new NotificationData(NotificationTarget.Ship, remotePlayer.name + " has died:\n" + Enum.GetName(typeof(DeathType), responseParams.GetInt("died")), 4f, true);
                            NotificationManager.SharedInstance.PostNotification(data, false);
                        }
                        else
                        {
                            var data = new NotificationData(NotificationTarget.Ship, remotePlayer.name + " has died:\n" + Enum.GetName(typeof(DeathType), responseParams.GetInt("died")), 4f, true);
                            NotificationManager.SharedInstance.PostNotification(data, false);
                        }
                    }
                    #endregion
                    break;
            }



        }

        private void OnRoomJoin(BaseEvent evt)
        {
            // Remove SFS2X listeners and re-enable interface before moving to the main game scene
            sfs.RemoveAllEventListeners();

            // Go to main game scene
            Resume();
        }

        private void OnRoomJoinError(BaseEvent evt)
        {
            // Show error message
            ModHelper.Console.WriteLine("Room join failed: " + (string)evt.Params["errorMessage"]);
        }

        private void Resume()
        {
            // Simulate "resume game" button press.
            var resume = FindObjectOfType<TitleScreenManager>().GetValue<SubmitActionLoadScene>("_resumeGameAction");
            if (resume == null) { resume = FindObjectOfType<TitleScreenManager>().GetValue<SubmitActionLoadScene>("_newGameAction"); }
            resume.Invoke("ConfirmSubmit");

        }

    }
}
