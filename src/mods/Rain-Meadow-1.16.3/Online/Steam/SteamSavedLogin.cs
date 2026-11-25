#if MEADOW_STEAM_PROBE
using System;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace RainMeadow
{
    internal static class SteamSavedLogin
    {
        private const string PreferencesName = "rainmeadow.steam.login";
        private const string TicketEntry = "ticket.v1";
        private const string KeyAlias = "rainmeadow.steam.login.v1";
        private const string CipherName = "AES/GCM/NoPadding";
        private const int IvSize = 12;
        private const int TagBits = 128;
        private const int MaxEncodedSize = 32768;

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Rain Meadow Steam Login");

        internal static SteamLoginTicket? Load()
        {
            try
            {
                using var preferences = OpenPreferences();
                string entry = preferences.Call<string>("getString", TicketEntry, "");
                if (string.IsNullOrEmpty(entry))
                    return null;
                if (entry.Length > MaxEncodedSize)
                    throw new InvalidDataException();

                string[] parts = entry.Split(':');
                if (parts.Length != 2)
                    throw new InvalidDataException();
                sbyte[] iv = ToJavaBytes(Convert.FromBase64String(parts[0]));
                sbyte[] encrypted = ToJavaBytes(Convert.FromBase64String(parts[1]));
                if (iv.Length != IvSize || encrypted.Length <= TagBits / 8)
                    throw new InvalidDataException();

                using var store = OpenKeyStore();
                using var key = GetKey(store);
                if (key == null)
                    return null;

                using var cipher = NewCipher();
                using var parameters = new AndroidJavaObject("javax.crypto.spec.GCMParameterSpec", TagBits, iv);
                cipher.Call("init", 2, key, parameters);
                sbyte[] plaintext = cipher.Call<sbyte[]>("doFinal", encrypted);
                try
                {
                    return DecodeTicket(plaintext);
                }
                finally
                {
                    Array.Clear(plaintext, 0, plaintext.Length);
                }
            }
            catch (Exception error)
            {
                Log.LogWarning($"Could not read saved Steam sign-in ({error.GetType().Name}).");
                return null;
            }
        }

        internal static bool Save(SteamLoginTicket ticket)
        {
            try
            {
                using var preferences = OpenPreferences();
                using var store = OpenKeyStore();
                using var key = GetKey(store) ?? CreateKey();
                using var cipher = NewCipher();
                cipher.Call("init", 1, key);
                sbyte[] plaintext = EncodeTicket(ticket);
                sbyte[] encrypted;
                try
                {
                    encrypted = cipher.Call<sbyte[]>("doFinal", plaintext);
                }
                finally
                {
                    Array.Clear(plaintext, 0, plaintext.Length);
                }

                byte[] iv = FromJavaBytes(cipher.Call<sbyte[]>("getIV"));
                string entry = Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(FromJavaBytes(encrypted));
                if (iv.Length != IvSize || entry.Length > MaxEncodedSize)
                    throw new InvalidDataException();

                using var editor = preferences.Call<AndroidJavaObject>("edit");
                using var updated = editor.Call<AndroidJavaObject>("putString", TicketEntry, entry);
                return updated.Call<bool>("commit");
            }
            catch (Exception error)
            {
                Log.LogWarning($"Could not save Steam sign-in ({error.GetType().Name}).");
                return false;
            }
        }

        internal static bool Clear()
        {
            bool removed = false;
            bool deletedKey = false;
            try
            {
                using var preferences = OpenPreferences();
                using var editor = preferences.Call<AndroidJavaObject>("edit");
                using var updated = editor.Call<AndroidJavaObject>("remove", TicketEntry);
                removed = updated.Call<bool>("commit");
            }
            catch (Exception error)
            {
                Log.LogWarning($"Could not remove saved Steam sign-in ({error.GetType().Name}).");
            }

            try
            {
                using var store = OpenKeyStore();
                if (store.Call<bool>("containsAlias", KeyAlias))
                    store.Call("deleteEntry", KeyAlias);
                deletedKey = true;
            }
            catch (Exception error)
            {
                Log.LogWarning($"Could not remove the Steam sign-in key ({error.GetType().Name}).");
            }

            return removed || deletedKey;
        }

        private static AndroidJavaObject OpenPreferences()
        {
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            return activity.Call<AndroidJavaObject>("getSharedPreferences", PreferencesName, 0);
        }

        private static AndroidJavaObject OpenKeyStore()
        {
            using var keyStore = new AndroidJavaClass("java.security.KeyStore");
            var store = keyStore.CallStatic<AndroidJavaObject>("getInstance", "AndroidKeyStore");
            try
            {
                store.Call("load", new object[] { null!, null! });
                return store;
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        private static AndroidJavaObject? GetKey(AndroidJavaObject store)
        {
            return store.Call<bool>("containsAlias", KeyAlias)
                ? store.Call<AndroidJavaObject>("getKey", KeyAlias, null!)
                : null;
        }

        private static AndroidJavaObject CreateKey()
        {
            const int encryptAndDecrypt = 3;
            using var builder = new AndroidJavaObject(
                "android.security.keystore.KeyGenParameterSpec$Builder", KeyAlias, encryptAndDecrypt);
            using var withBlockMode = builder.Call<AndroidJavaObject>("setBlockModes", (object)new[] { "GCM" });
            using var withPadding = withBlockMode.Call<AndroidJavaObject>("setEncryptionPaddings", (object)new[] { "NoPadding" });
            using var withSize = withPadding.Call<AndroidJavaObject>("setKeySize", 256);
            using var spec = withSize.Call<AndroidJavaObject>("build");
            using var generatorClass = new AndroidJavaClass("javax.crypto.KeyGenerator");
            using var generator = generatorClass.CallStatic<AndroidJavaObject>("getInstance", "AES", "AndroidKeyStore");
            generator.Call("init", spec);
            return generator.Call<AndroidJavaObject>("generateKey");
        }

        private static AndroidJavaObject NewCipher()
        {
            using var cipher = new AndroidJavaClass("javax.crypto.Cipher");
            return cipher.CallStatic<AndroidJavaObject>("getInstance", CipherName);
        }

        private static sbyte[] EncodeTicket(SteamLoginTicket ticket)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            try
            {
                writer.Write(ticket.AccountName);
                writer.Write(ticket.RefreshToken);
                var bytes = new sbyte[checked((int)stream.Length)];
                Buffer.BlockCopy(stream.GetBuffer(), 0, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                Array.Clear(stream.GetBuffer(), 0, stream.Capacity);
            }
        }

        private static SteamLoginTicket DecodeTicket(sbyte[] plaintext)
        {
            byte[] bytes = FromJavaBytes(plaintext);
            try
            {
                using var stream = new MemoryStream(bytes);
                using var reader = new BinaryReader(stream);
                var ticket = new SteamLoginTicket(reader.ReadString(), reader.ReadString());
                if (stream.Position != stream.Length)
                    throw new InvalidDataException();
                return ticket;
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private static sbyte[] ToJavaBytes(byte[] bytes)
        {
            var result = new sbyte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static byte[] FromJavaBytes(sbyte[] bytes)
        {
            var result = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }
    }
}
#endif
