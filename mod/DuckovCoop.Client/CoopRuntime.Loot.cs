using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private int _lastLootCode = int.MinValue;
        private bool _showContainer;
        private bool _showInventory;
        private float _containerViewRefreshAt;
        private float _inventoryRefreshAt;
        private uint _activeContainerId = WorldContainerId;
        private uint _lastSeenContainerViewId;
        private void UpdateInput()
        {
            UpdateNativeContainerSelection();

            if (UnityEngine.Input.GetKeyDown(KeyCode.E)
                && _client.State == ClientState.InGame)
            {
                _showContainer = !_showContainer;
                if (_showContainer)
                {
                    var requestId = _nearestNativeContainerId != 0 ? _nearestNativeContainerId : 0;
                    if (requestId != 0)
                    {
                        _client.SendContainerOpen(++_correlation, requestId);
                        _interaction = $"container opened id={requestId}";
                    }
                    else
                    {
                        _interaction = "no server container nearby";
                    }
                }
                else
                {
                    _interaction = "container closed";
                }
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.I) && _client.State == ClientState.InGame)
            {
                _showInventory = !_showInventory;
                if (_showInventory)
                {
                    Log("key: I inventory");
                    _client.SendContainerView(++_correlation, (uint)(1000 + _client.PlayerId));
                    _interaction = "inventory opened";
                }
                else
                {
                    _interaction = "inventory closed";
                }
            }
        }
        private void UpdateViews()
        {
            var lastContents = _client.LastContainerContents;
            if (lastContents != null &&
                lastContents.ContainerId != _lastSeenContainerViewId)
            {
                _lastSeenContainerViewId = lastContents.ContainerId;
                if (_lastSeenContainerViewId >= 100000)
                    _activeContainerId = _lastSeenContainerViewId;
            }

            if (_client.State == ClientState.InGame && _showContainer &&
                Time.unscaledTime >= _containerViewRefreshAt)
            {
                _containerViewRefreshAt = Time.unscaledTime + 1f;
                var requestId = _nearestNativeContainerId != 0
                    ? _nearestNativeContainerId
                    : _activeContainerId;
                if (requestId != 0)
                    _client.SendContainerOpen(++_correlation, requestId);
            }

            if (_client.State == ClientState.InGame && _showInventory &&
                Time.unscaledTime >= _inventoryRefreshAt)
            {
                _inventoryRefreshAt = Time.unscaledTime + 1f;
                _client.SendContainerView(++_correlation, (uint)(1000 + _client.PlayerId));
            }
        }
        private void UpdateResult()
        {
            if (_client.LastLoot != null && _client.LastLoot.Code != _lastLootCode)
            {
                _lastLootCode = _client.LastLoot.Code;
                Log($"loot result: code={_lastLootCode} itemId={_client.LastLoot.ItemId} count={_client.LastLoot.Count}");
            }
        }
    }
}
