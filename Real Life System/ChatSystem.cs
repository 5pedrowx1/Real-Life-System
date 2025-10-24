using GTA;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Real_Life_System
{
    public class ChatSystem
    {
        private readonly List<ChatDisplayMessage> displayMessages = new List<ChatDisplayMessage>();
        private readonly HashSet<string> displayedMessageIds = new HashSet<string>();
        private string currentInput = "";
        private bool isActive = false;
        private DateTime lastInputTime = DateTime.MinValue;
        private const int MAX_MESSAGES = 8;
        private const int MAX_INPUT_LENGTH = 100;
        private const float MESSAGE_LIFETIME = 10f;
        private const float FADE_START = 8f;
        private const float BASE_X = 0.012f;
        private const float BASE_Y = 0.52f;
        private const float LINE_HEIGHT = 0.025f;
        private const float INPUT_Y_OFFSET = 0.015f;
        private readonly Color backgroundColor = Color.FromArgb(0, 0, 0, 0);
        private readonly Color inputBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        private readonly Color textColor = Color.White;
        private readonly Color inputTextColor = Color.FromArgb(255, 255, 255, 255);
        private readonly Color playerNameColor = Color.FromArgb(255, 100, 200, 255); 
        private readonly Color systemMessageColor = Color.FromArgb(255, 255, 200, 100); 
        private readonly Color errorColor = Color.FromArgb(255, 255, 100, 100);
        public bool IsActive => isActive;
        public string CurrentInput => currentInput;

        public void AddMessage(string playerName, string message, ChatMessageType type = ChatMessageType.Normal)
        {
            var displayMsg = new ChatDisplayMessage
            {
                PlayerName = playerName,
                Message = message,
                ReceivedTime = DateTime.UtcNow,
                Type = type
            };

            switch (type)
            {
                case ChatMessageType.System:
                    displayMsg.CustomColor = systemMessageColor;
                    break;
                case ChatMessageType.Error:
                    displayMsg.CustomColor = errorColor;
                    break;
                case ChatMessageType.Me:
                    displayMsg.CustomColor = Color.FromArgb(255, 200, 150, 255); 
                    break;
                case ChatMessageType.Do:
                    displayMsg.CustomColor = Color.FromArgb(255, 150, 255, 150); 
                    break;
                case ChatMessageType.Private:
                    displayMsg.CustomColor = Color.FromArgb(255, 255, 255, 100); 
                    break;
                default:
                    displayMsg.CustomColor = textColor;
                    break;
            }

            displayMessages.Add(displayMsg);

            while (displayMessages.Count > MAX_MESSAGES)
            {
                displayMessages.RemoveAt(0);
            }

            if (type != ChatMessageType.System)
            {
                GTA.Audio.PlaySoundFrontendAndForget("CHAT_MESSAGE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            }
        }

        public void AddSystemMessage(string message)
        {
            AddMessage("SISTEMA", message, ChatMessageType.System);
        }

        public void AddErrorMessage(string message)
        {
            AddMessage("ERRO", message, ChatMessageType.Error);
        }

        public void Activate()
        {
            isActive = true;
            currentInput = "";
            lastInputTime = DateTime.UtcNow;
        }

        public void Deactivate()
        {
            isActive = false;
            currentInput = "";
        }

        public void AddCharacter(char c)
        {
            if (currentInput.Length < MAX_INPUT_LENGTH)
            {
                currentInput += c;
                lastInputTime = DateTime.UtcNow;
            }
        }

        public void RemoveCharacter()
        {
            if (currentInput.Length > 0)
            {
                currentInput = currentInput.Substring(0, currentInput.Length - 1);
                lastInputTime = DateTime.UtcNow;
            }
        }

        public string GetInputAndClear()
        {
            string input = currentInput;
            currentInput = "";
            return input;
        }

        public bool IsMessageDisplayed(string messageId)
        {
            return displayedMessageIds.Contains(messageId);
        }

        public void MarkMessageAsDisplayed(string messageId)
        {
            displayedMessageIds.Add(messageId);

            if (displayedMessageIds.Count > 100)
            {
                var oldest = displayedMessageIds.Take(50).ToList();
                foreach (var id in oldest)
                {
                    displayedMessageIds.Remove(id);
                }
            }
        }

        public void Draw()
        {
            try
            {
                var now = DateTime.UtcNow;

                displayMessages.RemoveAll(m => (now - m.ReceivedTime).TotalSeconds > MESSAGE_LIFETIME);

                int visibleCount = Math.Min(displayMessages.Count, MAX_MESSAGES);
                float currentY = BASE_Y;

                if (isActive)
                {
                    DrawInputBox(currentY);
                    currentY -= INPUT_Y_OFFSET;
                }

                for (int i = displayMessages.Count - 1; i >= Math.Max(0, displayMessages.Count - visibleCount); i--)
                {
                    var msg = displayMessages[i];
                    float age = (float)(now - msg.ReceivedTime).TotalSeconds;
                    int alpha = 255;

                    if (!isActive)
                    {
                        if (age > FADE_START)
                        {
                            float fadeProgress = (age - FADE_START) / (MESSAGE_LIFETIME - FADE_START);
                            alpha = (int)(255 * (1 - fadeProgress));
                        }
                    }

                    if (alpha > 0)
                    {
                        DrawMessage(msg, currentY, alpha);
                        currentY -= LINE_HEIGHT;
                    }
                }
            }
            catch
            {
                // Silently fail
            }
        }

        private void DrawMessage(ChatDisplayMessage msg, float y, int alpha)
        {
            float bgWidth = 0.35f;
            float bgHeight = LINE_HEIGHT - 0.002f;

            Color bgColor = Color.FromArgb(
                Math.Min(backgroundColor.A, alpha),
                backgroundColor.R,
                backgroundColor.G,
                backgroundColor.B
            );

            DrawRect(BASE_X, y, bgWidth, bgHeight, bgColor);

            string fullText;
            Color nameColor;

            switch (msg.Type)
            {
                case ChatMessageType.System:
                case ChatMessageType.Error:
                    fullText = $"[{msg.PlayerName}] {msg.Message}";
                    nameColor = msg.CustomColor;
                    break;
                case ChatMessageType.Me:
                    fullText = $"* {msg.PlayerName} {msg.Message}";
                    nameColor = msg.CustomColor;
                    break;
                case ChatMessageType.Do:
                    fullText = $"* {msg.Message} (({msg.PlayerName}))";
                    nameColor = msg.CustomColor;
                    break;
                case ChatMessageType.Private:
                    fullText = $"[PM de {msg.PlayerName}] {msg.Message}";
                    nameColor = msg.CustomColor;
                    break;
                default:
                    fullText = $"{msg.PlayerName}: {msg.Message}";
                    nameColor = playerNameColor;
                    break;
            }

            Color textColorWithAlpha = Color.FromArgb(
                Math.Min(255, alpha),
                nameColor.R,
                nameColor.G,
                nameColor.B
            );

            DrawText(fullText, BASE_X + 0.005f, y - 0.003f, 0.35f, textColorWithAlpha);
        }

        private void DrawInputBox(float y)
        {
            float bgWidth = 0.35f;
            float bgHeight = LINE_HEIGHT + 0.005f;

            DrawRect(BASE_X, y, bgWidth, bgHeight, inputBackgroundColor);
            DrawText(">", BASE_X + 0.005f, y - 0.003f, 0.015f, Color.FromArgb(255, 150, 255, 150));

            string displayText = currentInput;

            if ((DateTime.UtcNow.Millisecond / 500) % 2 == 0)
            {
                displayText += "_";
            }

            DrawText(displayText, BASE_X + 0.018f, y - 0.003f, 0.33f, inputTextColor);

            string counter = $"{currentInput.Length}/{MAX_INPUT_LENGTH}";
            Color counterColor = currentInput.Length >= MAX_INPUT_LENGTH
                ? errorColor
                : Color.FromArgb(150, 255, 255, 255);

            DrawText(counter, BASE_X + bgWidth - 0.04f, y - 0.003f, 0.04f, counterColor);
        }

        private void DrawRect(float x, float y, float width, float height, Color color)
        {
            Function.Call(Hash.DRAW_RECT,
                x, y, width, height,
                color.R, color.G, color.B, color.A);
        }

        private void DrawText(string text, float x, float y, float maxWidth, Color color)
        {
            var textElement = new TextElement(
                text,
                new PointF(x * GTA.UI.Screen.Width, y * GTA.UI.Screen.Height),
                0.35f, 
                color,
                GTA.UI.Font.ChaletLondon, 
                GTA.UI.Alignment.Left,
                false, 
                true,  
                (int)(maxWidth * GTA.UI.Screen.Width)
            );

            textElement.Draw();
        }

        public ChatCommand ProcessCommand(string input, string senderName)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var cmd = new ChatCommand();

            if (input.StartsWith("/me "))
            {
                cmd.Type = ChatMessageType.Me;
                cmd.Message = input.Substring(4);
                cmd.SenderName = senderName;
                return cmd;
            }
            else if (input.StartsWith("/do "))
            {
                cmd.Type = ChatMessageType.Do;
                cmd.Message = input.Substring(4);
                cmd.SenderName = senderName;
                return cmd;
            }
            else if (input.StartsWith("/pm "))
            {
                var parts = input.Substring(4).Split(new[] { ' ' }, 2);
                if (parts.Length >= 2)
                {
                    cmd.Type = ChatMessageType.Private;
                    cmd.TargetPlayer = parts[0];
                    cmd.Message = parts[1];
                    cmd.SenderName = senderName;
                    return cmd;
                }
                return null;
            }
            else if (input.StartsWith("/clear"))
            {
                displayMessages.Clear();
                AddSystemMessage("Chat limpo");
                return null;
            }
            else if (input.StartsWith("/help"))
            {
                AddSystemMessage("Comandos disponíveis:");
                AddSystemMessage("/me [ação] - Ação roleplay");
                AddSystemMessage("/do [descrição] - Descrição RP");
                AddSystemMessage("/pm [player] [msg] - Mensagem privada");
                AddSystemMessage("/clear - Limpar chat");
                return null;
            }
            else
            {
                cmd.Type = ChatMessageType.Normal;
                cmd.Message = input;
                cmd.SenderName = senderName;
                return cmd;
            }
        }
    }
}