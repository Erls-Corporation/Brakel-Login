﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using PushNotifications;
using System.Data.SqlClient;

namespace BrakelInlogApplication
{
	/// <summary>
	/// The actual implementation of the API calls happens in this helper class
	/// </summary>
	public sealed class APIHelper
	{
		/// <summary>
		/// The private instance for easy instantiation
		/// </summary>
		private static APIHelper _instance = new APIHelper();
		/// <summary>
		/// The Instance of this class
		/// </summary>
		public static APIHelper Instance { get { return _instance; } }

		/// <summary>
		/// Private to prevent instantiation
		/// </summary>
		private APIHelper()
		{
			//Register event handler for background polling
			BackgroundPoller.Instance.OnResultChanged += OnPollingResult;
		}

		/// <summary>
		/// Validates the user's credentials and returns a token that will be used to validate other requests
		/// </summary>
		/// <param name="username">The username</param>
		/// <param name="passwordHash">The hashed password</param>
		/// <returns>A token not equal to all 0 on succes, a token of all 0 on failure</returns>
		public Guid login(string username, string passwordHash)
		{
			Guid userToken = Guid.Empty;

			using (SqlConnection connection = new SqlConnection(ConstantHelper.ConnectionString))
			{
				connection.Open();

				// perform work with connection
				string query = String.Format("SELECT [hash] FROM [user] WHERE [username] = '{0}'", username);
				SqlCommand command = new SqlCommand(query, connection);
				string sqlHash = command.ExecuteScalar() as String;

				//validate credentials
				if (passwordHash.Equals(sqlHash, StringComparison.OrdinalIgnoreCase))
				{
					//generate token
					userToken = Guid.NewGuid();

					//register token in db to the user
					query = String.Format("INSERT INTO [token] ([username], [token], [createDateTime]) VALUES('{0}','{1}','{2}')", username, userToken, DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss"));
					command = new SqlCommand(query, connection);
					int result = command.ExecuteNonQuery();
					if (result < 1)
					{
						userToken = Guid.Empty;
					}
				}
			}

			//return id
			return userToken;
		}

		/// <summary>
		/// Returns a list of buildngs which the current user can see, and the permissions he has in those buildings
		/// </summary>
		/// <param name="userToken">The current user's token</param>
		/// <returns>The list of Buildings</returns>
		public List<Building> getBuildings(Guid userToken)
		{
			List<Building> buildings = new List<Building>();

			using (SqlConnection connection = new SqlConnection(ConstantHelper.ConnectionString))
			{
				connection.Open();
				string query = String.Format("SELECT [username] FROM [token] WHERE [token] = '{0}'", userToken);
				SqlCommand command = new SqlCommand(query, connection);

				//Validate token
				string username = command.ExecuteScalar() as String;
				if (String.IsNullOrWhiteSpace(username))
				{
					throw new APIException("The provided userToken is invalid or has expired", "userToken");
				}
				else
				{
					//join buildings on users rights
					query = String.Format(@"SELECT	[building].*, [userBuildingCouple].[accessRights] FROM [building]
													LEFT JOIN [userBuildingCouple] ON building.buildingId = [userBuildingCouple].[buildingId]
													LEFT JOIN [user] ON [user].[userId] = [userBuildingCouple].[userId]
											WHERE	[user].[username] = '{0}'", username);
					command = new SqlCommand(query, connection);

					//Fill collection
					SqlDataReader reader = command.ExecuteReader();
					while (reader.Read())
					{
						buildings.Add(new Building()
						{
							AccessRole = Building.ParseAccessRightsFromString(reader["accessRights"].ToString()),
							BuildingID = Int32.Parse(reader["buildingId"].ToString()),
							BuildingName = reader["name"].ToString(),
							Parent = Int32.Parse(reader["parentId"].ToString())
						});
					}
				}
			}
			//return collection
			return buildings;
		}

		/// <summary>
		/// Method to iniate making changes to groups
		/// </summary>
		/// <param name="userToken">The user token</param>
		/// <param name="buildingId">The building id for the building in which the groups are</param>
		/// <param name="changes">The list of changes you want to commit</param>
		/// <returns>The list of changes with a boolean value to indicate succes of the operation per change</returns>
		public List<Changes> makeChangesToGroups(Guid userToken, int buildingId, List<Changes> changes)
		{
			if (userToken != Guid.Empty)
			{
				if (buildingId != 0)
				{
					//Send to building, wait for initial result
					foreach (var item in changes)
					{
						item.ChangeStatus = true;
					}

					//Start background polling					
					BackgroundPoller.Instance.StartPollingBuilding(userToken, buildingId);

					//return initial result
					return changes;
				}
				else
				{
					throw new APIException("The provided buildingId is invalid", "buildingId");
				}
			}
			else
			{
				throw new APIException("The provided userToken is invalid or expired", "userToken");
			}
		}

		/// <summary>
		/// Get the current user's screen layout for the selected building
		/// </summary>
		/// <param name="userToken">The current user's token</param>
		/// <param name="buildingId">The building for which you want the layout</param>
		/// <returns>A string representation of the XML, which describes the layout of the application</returns>
		public string getUserLayout(Guid userToken, int buildingId)
		{
			string resultLayout = "";

			using (SqlConnection connection = new SqlConnection(ConstantHelper.ConnectionString))
			{
				connection.Open();
				string query = String.Format("SELECT [username] FROM [token] WHERE [token] = '{0}'", userToken);
				SqlCommand command = new SqlCommand(query, connection);

				//Validate token
				string username = command.ExecuteScalar() as String;
				if (String.IsNullOrWhiteSpace(username))
				{
					throw new APIException("The provided userToken is invalid or has expired", "userToken");
				}
				else
				{
					//get the layout for the user - building combination
					query = String.Format(@"SELECT	[userBuildingCouple].[screenLayout] FROM [userBuildingCouple]													
													LEFT JOIN [user] ON [user].[userId] = [userBuildingCouple].[userId]
											WHERE	[user].[username] = '{0}' and [userBuildingCouple].[buildingId] = {1}", username, buildingId);
					command = new SqlCommand(query, connection);

					//get the value and store it in the string
					SqlDataReader reader = command.ExecuteReader();
					if (reader.HasRows)
					{
						while (reader.Read())
						{
							resultLayout = reader["screenLayout"].ToString();
						}
					}
					else
					{
						throw new APIException("The provided buildingId is invalid", "buildingId");
					}
				}
			}

			//return layout json as a string
			return resultLayout;
		}

		/// <summary>
		/// Eventhandler for the polling result change
		/// </summary>
		/// <param name="userToken">The userToken of the user who initiated the poll</param>
		/// <param name="buildingId">The building this result is about</param>
		/// <param name="json">The result in JSON format</param>
		public static void OnPollingResult(Guid userToken, int buildingId, string json)
		{
			string deviceID = ""; //get from database based on userToken
			string message = String.Format(@"{{ ""building"":{0}, ""result"":{1} }}", buildingId, json);
			PushNotification.SendPushNotification(deviceID, message);
		}

		public List<Floor> getFloors(Guid userToken, int buildingId)
		{
			List<Floor> floors = new List<Floor>();

			using (SqlConnection connection = new SqlConnection(ConstantHelper.ConnectionString))
			{
				connection.Open();
				string query = String.Format("SELECT [username] FROM [token] WHERE [token] = '{0}'", userToken);
				SqlCommand command = new SqlCommand(query, connection);

				//Validate token
				string username = command.ExecuteScalar() as String;
				if (String.IsNullOrWhiteSpace(username))
				{
					throw new APIException("The provided userToken is invalid or has expired", "userToken");
				}
				else
				{
					//join buildings on users rights
					query = String.Format(@"SELECT	[building].*, [userBuildingCouple].[accessRights] FROM [building]
													LEFT JOIN [userBuildingCouple] ON building.buildingId = [userBuildingCouple].[buildingId]
													LEFT JOIN [user] ON [user].[userId] = [userBuildingCouple].[userId]
											WHERE	[user].[username] = '{0}' and [building].[parentId] = {1}", username, buildingId);
					command = new SqlCommand(query, connection);

					//Fill collection
					SqlDataReader reader = command.ExecuteReader();
					while (reader.Read())
					{
						floors.Add(new Floor()
						{
							AccessRole = Building.ParseAccessRightsFromString(reader["accessRights"].ToString()),
							BuildingID = Int32.Parse(reader["buildingId"].ToString()),
							BuildingName = reader["name"].ToString(),
							Parent = Int32.Parse(reader["parentId"].ToString())
						});
					}
				}
			}
			//return collection
			return floors;
		}

		public List<Room> getRooms(Guid userToken, int floorId)
		{
			return null;
		}
	}
}