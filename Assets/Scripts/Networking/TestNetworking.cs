﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using UnityEngine.UI;
using MLAPI;

public class TestNetworking : NetworkedBehaviour {
    public bool makemehost;
    public GameObject MapPrefab;

    void Start() {
        if (makemehost) {
            NetworkingManager.singleton.StartHost();
            Debug.Log("Starting map...");
            GameObject Map = Instantiate(MapPrefab);
            Map.transform.position = new Vector3(30f, 0, 0);
            Map.GetComponent<NetworkedObject>().Spawn();

            Destroy(transform.parent.gameObject, 1f);
            Destroy(gameObject);
        }
        var input = gameObject.GetComponent<InputField>();
        var se = new InputField.SubmitEvent();
        se.AddListener(SubmitName);
        input.onEndEdit = se;

        //or simply use the line below, 
        //input.onEndEdit.AddListener(SubmitName);  // This also works
    }

    private void SubmitName(string ip) {
        NetworkingManager.singleton.NetworkConfig.ConnectAddress = ip;
        NetworkingManager.singleton.StartClient();
        Destroy(transform.parent.gameObject, 1f);
        Destroy(gameObject);
    }
}