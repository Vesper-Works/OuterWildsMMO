﻿using Sfs2X;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using System.Collections;
using Sfs2X.Core;
using Sfs2X.Entities;

namespace OuterWildsOnline
{
    enum ChatMode
    {   NA,
        Universe,
        TimberHearth,
        BrittleHollow,
        GiantsDeep,
        DarkBramble,
        TheInterloper,
        QuantumMoon,
        TheStranger,
        TheAttlerock,
        HollowsLantern,
        Eye,
        HourglassTwins,
        SunStation,
        DreamWorld,
        WhiteHoleStation,
        Campfire
    }
    class ChatHandler : MonoBehaviour
    {
        private SmartFox sfs { get => ConnectionController.Connection; }

        private Dictionary<ChatMode, bool> allowedChatModes = new Dictionary<ChatMode, bool>();
        private Dictionary<ChatMode, Text> chatBoxes = new Dictionary<ChatMode, Text>();

        private bool NoChatsAvailable { get => !allowedChatModes.ContainsValue(true); }

        private GameObject inputBox;
        private GameObject inputText;

        private GameObject nameField;

        private Text inputFieldText;
        private Text nameFieldText;

        private NotificationData shipAloneInSpace;
        //private NotificationData shipShowChat;
        private int shipMessageCount;

        private bool selected;

        private ScreenPrompt exitChatPrompt;
        private ScreenPrompt enterChatPrompt;

        public static ChatMode chatMode;
        private void Start()
        {
            shipAloneInSpace = new NotificationData(NotificationTarget.Ship, "Alone in space", 5f, true);

            enterChatPrompt = new ScreenPrompt("ENTER to start chatting");
            exitChatPrompt = new ScreenPrompt("ESC to stop chatting");

            chatMode = ChatMode.Universe;

            inputBox = Instantiate(GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/NumericalReadout/GravityText"), GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/NumericalReadout").transform);

            inputText = Instantiate(GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/NumericalReadout/GravityText"), inputBox.transform);
            nameField = Instantiate(GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/NumericalReadout/GravityText"), inputBox.transform);

            GlobalMessenger.AddListener("SuitUp", new Callback(this.OnPutOnSuit));
            GlobalMessenger.AddListener("RemoveSuit", new Callback(this.OnRemoveSuit));

            Invoke("LateStart", 2f);

            int count = 0;
            foreach (ChatMode mode in Enum.GetValues(typeof(ChatMode)))
            {
                allowedChatModes.Add(mode, false);
                chatBoxes[mode] = Instantiate(GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/NumericalReadout/GravityText"), GameObject.Find("PlayerHUD/HelmetOnUI/UICanvas/SecondaryGroup/GForce/NumericalReadout").transform).GetComponent<Text>();
                count++;
            }
            sfs.AddEventListener(Sfs2X.Core.SFSEvent.PUBLIC_MESSAGE, OnPublicMessage);
        }

        private void OnPutOnSuit()
        {
            Locator.GetPromptManager().AddScreenPrompt(enterChatPrompt, PromptPosition.UpperRight, true);
            allowedChatModes[ChatMode.Universe] = true;

        }
        private void OnRemoveSuit()
        {
            Locator.GetPromptManager().RemoveScreenPrompt(enterChatPrompt);
        }

        private void Update()
        {
            if (OWInput.GetInputMode() != InputMode.Character && OWInput.GetInputMode() != InputMode.Roasting && OWInput.GetInputMode() != InputMode.KeyboardInput) { return; }

            if (selected)
            {
                if (Keyboard.current.enterKey.wasPressedThisFrame)
                {
                    if (inputFieldText.text.Trim(' ') == "") { return; }
                    sfs.Send(new Sfs2X.Requests.PublicMessageRequest(chatMode.ToString() + "ʣ" + inputFieldText.text));
                    inputFieldText.text = "";
                }
                foreach (var key in Keyboard.current.allKeys)
                {
                    if (key.wasPressedThisFrame && key.displayName.Length == 1)
                    {
                        if (Keyboard.current.shiftKey.isPressed)
                        {
                            if (Keyboard.current.digit1Key.wasPressedThisFrame)
                            {
                                inputFieldText.text += "!";
                                FormatText(inputFieldText);
                            }
                            if (Keyboard.current.slashKey.wasPressedThisFrame)
                            {
                                inputFieldText.text += "?";
                                FormatText(inputFieldText);
                            }
                            else
                            {
                                inputFieldText.text += key.displayName.ToUpper();
                                FormatText(inputFieldText);
                            }
                        }
                        else
                        {
                            inputFieldText.text += key.displayName.ToLower();
                            FormatText(inputFieldText);
                        }
                    }
                }
                if (Keyboard.current.backspaceKey.wasPressedThisFrame)
                {
                    inputFieldText.text = inputFieldText.text.Remove(inputFieldText.text.Length - 1);
                    StartCoroutine(DeleteCharacters());
                }
                if (Keyboard.current.spaceKey.wasPressedThisFrame)
                {
                    inputFieldText.text += " ";
                }
                if (Keyboard.current.tabKey.wasPressedThisFrame)
                {
                    UpdateOpenChat();
                }
        
            }
            if (Keyboard.current.enterKey.wasPressedThisFrame && !selected && chatMode != ChatMode.NA)
            {
                OWInput.ChangeInputMode(InputMode.KeyboardInput);
                selected = true;
                Locator.GetPromptManager().AddScreenPrompt(exitChatPrompt, PromptPosition.UpperRight, true);
                Locator.GetPromptManager().RemoveScreenPrompt(enterChatPrompt);
            }
            if (Keyboard.current.escapeKey.wasPressedThisFrame && selected)
            {
                OWInput.ChangeInputMode(InputMode.Character);
                selected = false;
                Locator.GetPromptManager().AddScreenPrompt(enterChatPrompt, PromptPosition.UpperRight, true);
                Locator.GetPromptManager().RemoveScreenPrompt(exitChatPrompt);
            }
        }

        private IEnumerator DeleteCharacters()
        {
            yield return new WaitForSeconds(1f);

            while (Keyboard.current.backspaceKey.isPressed)
            {
                inputFieldText.text = inputFieldText.text.Remove(inputFieldText.text.Length - 1);
                yield return new WaitForSeconds(0.2f);
            }
        }

        private void UpdateOpenChat()
        {

            if (NoChatsAvailable)
            {
                nameFieldText.text = "Alone in space";
                if (PlayerState.AtFlightConsole())
                {
                    NotificationManager.SharedInstance.PostNotification(shipAloneInSpace, true);
                }

            }
            else
            {
                chatBoxes[chatMode].gameObject.SetActive(false);
                NotificationManager.SharedInstance.UnpinNotification(shipAloneInSpace);
                IterateChatMode();
                nameFieldText.text = chatMode.ToString() + " (" + sfs.MySelf.Name + "):";
                chatBoxes[chatMode].gameObject.SetActive(true);
            }
        }

        private void IterateChatMode()
        {
            if (NoChatsAvailable) { return; }
            chatMode++;
            if (((int)chatMode) == 17)
            {
                chatMode = 0;
            }
            if (!allowedChatModes[chatMode] || chatMode != ChatMode.Universe) { IterateChatMode(); }
        }
        private IEnumerator GetAllowedChatModes()
        {
            while (true)
            {
                foreach (var astroObject in FindObjectsOfType<AstroObject>())
                {
                    ChatMode astroChatMode = GetChatModeFromAstroObjectName(astroObject.GetAstroObjectName().ToString());
                    if (PlayerState.InBrambleDimension() || PlayerState.InDreamWorld()) { break; }
                    if (Vector3.Distance(Locator.GetPlayerTransform().position, astroObject.transform.position) < 600)
                    {
                        if (allowedChatModes[astroChatMode] == false && astroChatMode != ChatMode.NA)
                        {
                            if (PlayerState.AtFlightConsole())
                            {
                                var data = new NotificationData(NotificationTarget.Ship, "Arrived at: " + astroChatMode.ToString(), 4f, true);
                                NotificationManager.SharedInstance.PostNotification(data, false);
                            }
                            else
                            {
                                var data = new NotificationData(NotificationTarget.Player, "Arrived at: " + astroChatMode.ToString(), 4f, true);
                                NotificationManager.SharedInstance.PostNotification(data, false);
                            }

                        }
                        bool onlyChat = NoChatsAvailable;
                        allowedChatModes[astroChatMode] = true;
                        if (onlyChat)
                        {
                            UpdateOpenChat();
                        }
                    }
                    else
                    {
                        allowedChatModes[astroChatMode] = false;
                        if (chatMode == astroChatMode)
                        {
                            UpdateOpenChat();
                        }
                    }
                }
                //foreach (var campfire in FindObjectsOfType<Campfire>())
                //{
                //    ChatMode astroChatMode = ChatMode.Campfire;
                //    if (Mathf.Abs(Vector3.Distance(Locator.GetPlayerTransform().position, campfire.transform.position)) < 5)
                //    {
                //        allowedChatModes[astroChatMode] = true;
                //    }
                //    else
                //    {
                //        allowedChatModes[astroChatMode] = false;
                //        if (chatMode == astroChatMode)
                //        {
                //            IterateChatMode();
                //        }
                //    }
                //}
                if (PlayerState.InDreamWorld())
                {
                    allowedChatModes[ChatMode.DreamWorld] = true;
                }
                else
                {
                    allowedChatModes[ChatMode.DreamWorld] = false;
                }
                if (PlayerState.InBrambleDimension())
                {
                    allowedChatModes[ChatMode.DarkBramble] = true;
                }
                else
                {
                    allowedChatModes[ChatMode.DarkBramble] = false;
                }

                yield return new WaitForSeconds(2f);
            }
        }
        private ChatMode GetChatModeFromAstroObjectName(string astroObjectName)
        {
            switch (astroObjectName)
            {
                case "Comet":
                    return ChatMode.TheInterloper;
                case "RingWorld":
                    return ChatMode.TheStranger;
                case "TimberMoon":
                    return ChatMode.TheAttlerock;
                case "VolcanicMoon":
                    return ChatMode.HollowsLantern;
                case "WhiteHole":
                    return ChatMode.WhiteHoleStation;
                default:
                    ChatMode tempChatMode;
                    if (Enum.TryParse(astroObjectName, out tempChatMode))
                    {
                        return tempChatMode;
                    }
                    return ChatMode.NA;
            }
        }
        private void LateStart()
        {
            inputFieldText = inputText.GetComponent<Text>();
            inputFieldText.horizontalOverflow = HorizontalWrapMode.Overflow;
            inputFieldText.verticalOverflow = VerticalWrapMode.Overflow;
            inputFieldText.alignByGeometry = false;
            inputFieldText.alignment = TextAnchor.LowerLeft;
            inputFieldText.text = "";

            nameField.transform.localPosition += new Vector3(-0.15f, 0, 0);
            nameFieldText = nameField.GetComponent<Text>();
            nameFieldText.horizontalOverflow = HorizontalWrapMode.Overflow;
            nameFieldText.verticalOverflow = VerticalWrapMode.Overflow;
            nameFieldText.alignByGeometry = false;
            nameFieldText.alignment = TextAnchor.UpperRight;
            nameFieldText.text = chatMode.ToString() + " (" + sfs.MySelf.Name + "):";

            inputBox.transform.position += new Vector3(1, -0.15f, 0);
            foreach (var chatBoxText in chatBoxes.Values)
            {
                chatBoxText.transform.position += new Vector3(1, -0.1f, 0);
                chatBoxText.horizontalOverflow = HorizontalWrapMode.Overflow;
                chatBoxText.verticalOverflow = VerticalWrapMode.Overflow;
                chatBoxText.alignByGeometry = false;
                chatBoxText.alignment = TextAnchor.LowerLeft;
                chatBoxText.text = "";
            }
            StartCoroutine(GetAllowedChatModes());

            Destroy(inputBox.GetComponent<Text>());
        }
        private void FormatText(Text text)
        {
            if (text.text.Length % 40 == 0) { text.text += Environment.NewLine; }
        }
        private string FormatTextFirstTime(string text)
        {
            var numOfLines = Mathf.FloorToInt(text.Length / 40);

            for (int i = 1; i < numOfLines; i++)
            {
                text = text.Insert(i * 40, Environment.NewLine);
            }
            return text;
        }

        private void OnPublicMessage(BaseEvent evt)
        {
            User sender = (User)evt.Params["sender"];
            string[] message = ((string)evt.Params["message"]).Split('ʣ');
            ChatMode chatmode = (ChatMode)Enum.Parse(typeof(ChatMode), message[0]);
            if(chatmode == ChatMode.NA) { return; }
            chatBoxes[chatMode].text += "\n" + sender.Name + ": " + message[1];
            if (PlayerState.AtFlightConsole())
            {
                shipMessageCount++;
                var data = new NotificationData(NotificationTarget.Ship, "\n" + sender.Name + ": " + message[1], 5f, true);
                NotificationManager.SharedInstance.PostNotification(data, false);
            }
        }
    }
}
