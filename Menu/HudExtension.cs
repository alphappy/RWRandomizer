﻿using Archipelago.MultiClient.Net.MessageLog.Messages;
using HUD;
using Menu;
using RWCustom;
using System.Collections.Generic;
using UnityEngine;

namespace RainWorldRandomizer
{
    public static class HudExtension
    {
        public static ChatLog chatLog;

        public static void ApplyHooks()
        {
            On.HUD.HUD.InitSinglePlayerHud += OnInitSinglePlayerHud;
        }

        public static void RemoveHooks()
        {
            On.HUD.HUD.InitSinglePlayerHud -= OnInitSinglePlayerHud;
        }

        private static void OnInitSinglePlayerHud(On.HUD.HUD.orig_InitSinglePlayerHud orig, HUD.HUD self, RoomCamera cam)
        {
            orig(self, cam);

            chatLog = new ChatLog(self, self.fContainers[1]);
            self.AddPart(chatLog);
        }
    }

    public class ChatLog : HudPart
    {
        private const int MAX_MESSAGES = 5;
        protected const float MSG_SIZE_X = 250;
        protected const float MSG_SIZE_Y = 35f;

        private FContainer container;
        private Queue<ChatMessage> messages = new Queue<ChatMessage>();

        public Vector2 pos;

        public ChatLog(HUD.HUD hud, FContainer container) : base(hud)
        {
            this.container = container;
            pos = new Vector2(hud.rainWorld.options.ScreenSize.x - MSG_SIZE_X - 50f, Mathf.Max(50f, hud.rainWorld.options.SafeScreenOffset.y + 17.25f));

            AddMessage("This is a test message");
            AddMessage("This is a second test message");
        }

        public void AddMessage(string text)
        {
            EnqueueMessage(new ChatMessage(this, text));
        }

        public void AddMessage(LogMessage logMessage)
        {
            EnqueueMessage(new ChatMessage(this, logMessage));
        }

        private void EnqueueMessage(ChatMessage message)
        {
            // Increment indices
            foreach (ChatMessage msg in messages)
            {
                msg.index++;
            }

            // Remove oldest message if reached message limit
            if (messages.Count == MAX_MESSAGES)
            {
                messages.Dequeue().ClearSprites();
            }

            // Add message
            messages.Enqueue(message);
        }

        // TODO: Hide when paused
        public override void Draw(float timeStacker)
        {
            base.Draw(timeStacker);

            foreach (ChatMessage msg in messages)
            {
                msg.Draw(timeStacker);
            }
        }

        private class ChatMessage
        {
            ChatLog owner;

            public int index;
            public string text;
            FSprite backgroundSprite;
            FLabel[] messageLabels;

            // Constructor for "simple" messages (Only one message part)
            // TODO: Text wrapping
            public ChatMessage(ChatLog chatLog, string message)
            {
                owner = chatLog;
                index = 0;
                text = message;

                // TODO: Make backdrop stand out on map view
                backgroundSprite = new FSprite("pixel")
                {
                    color = Menu.Menu.MenuRGB(Menu.Menu.MenuColors.Black),
                    x = chatLog.pos.x + (MSG_SIZE_X / 2),
                    scaleX = MSG_SIZE_X,
                    scaleY = MSG_SIZE_Y,
                    alpha = 0.8f
                };
                chatLog.container.AddChild(backgroundSprite);

                // Only need one label for simple message
                messageLabels = new FLabel[1]
                {
                    new FLabel(Custom.GetFont(), message)
                    {
                        x = chatLog.pos.x + 10f,
                        alignment = FLabelAlignment.Left,
                    }
                };
                chatLog.container.AddChild(messageLabels[0]);
            }

            // Constructor for complex messages using an Archipelago LogMessage
            // TODO: Implement splitting LogMessages with multiple labels
            public ChatMessage(ChatLog chatLog, LogMessage message)
            {

            }

            // TODO: Fade messages over time
            // TODO: Make new messages push onto screen instead of instant update
            public void Draw(float timeStacker)
            {
                backgroundSprite.y = owner.pos.y + (index * MSG_SIZE_Y);
                foreach (FLabel label in messageLabels)
                {
                    label.y = owner.pos.y + (index * MSG_SIZE_Y);
                }
            }

            public void ClearSprites()
            {
                backgroundSprite.RemoveFromContainer();
                foreach (FLabel label in messageLabels)
                {
                    label.RemoveFromContainer();
                }
            }
        }
    }
}
