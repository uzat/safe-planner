module Conference.State

open Elmish
open Elmish.Helper
open Global

open Server.ServerTypes
open EventSourced

open Conference.Types
open Conference.Ws
open Domain
open Domain.Model
open App.Server
open Application

let private eventIsForConference (ConferenceId conferenceId) envelope =
  envelope.Metadata.Source = conferenceId

let private updateStateWithEvents conference events  =
  events
  |> List.fold Domain.Projections.evolve conference


let private queryConference conferenceId =
  // TODO react to query Error
  Cmd.OfAsync.perform conferenceApi.conference conferenceId ConferenceLoaded

let private queryConferences =
  Cmd.OfAsync.perform conferenceApi.conferences () ConferencesLoaded

let private queryOrganizers =
  Cmd.OfAsync.perform organizerApi.organizers () OrganizersLoaded

let init (user : UserData)  =
  {
    View = CurrentView.NotAsked
    Conferences = RemoteData.NotAsked
    Organizers = RemoteData.NotAsked
    LastEvents = None
    Organizer = user.OrganizerId
    TransactionSubscriptions = []
    OpenNotifications = []
  }, Cmd.ofSub <| startWs user.Token

let dispose () =
  Cmd.ofSub stopWs

let private timeoutCmd timeout msg dispatch =
  Browser.Dom.window.setTimeout((fun _ -> msg |> dispatch), timeout) |> ignore

let private withView view model =
  { model with View = view }

let private withTransactionSubscription transaction model =
   { model with TransactionSubscriptions = transaction :: model.TransactionSubscriptions}

let private withTransactionSubscriptions transactions model =
  { model with TransactionSubscriptions = List.concat [transactions ; model.TransactionSubscriptions ] }

let private withReceivedEvents eventEnvelopes model =
  let groupedByTransaction =
    eventEnvelopes
    |> List.groupBy (fun envelope -> envelope.Metadata.Transaction)

  let notifications =
    groupedByTransaction
    |> List.collect (fun (transaction,events) ->
        if model.TransactionSubscriptions |> List.contains transaction
        then events |> List.map (fun envelope -> envelope.Event,transaction,Entered)
        else [])

  let transactions =
    groupedByTransaction |> List.map fst

  let openTransactions =
    model.TransactionSubscriptions
    |> List.filter (fun tx -> transactions |> List.contains tx |> not)

  let model =
    { model with
        LastEvents = Some eventEnvelopes
        TransactionSubscriptions =  openTransactions
        OpenNotifications = model.OpenNotifications @ notifications
    }

  let commands =
    notifications
    |> List.map (RequestNotificationForRemoval>>(timeoutCmd 5000)>>Cmd.ofSub)
    |> Cmd.batch

  model |> withCommand commands

let withRequestedForRemovalNotification (notification,transaction,_) model =
  let mapper (event,tx,animation) =
    if event = notification && tx = transaction then
      (event,tx,Leaving)
    else
      (event,tx,animation)

  let cmd =
    (notification,transaction,Leaving)
    |> RemoveNotification
    |> timeoutCmd 2000
    |> Cmd.ofSub

  { model with OpenNotifications = model.OpenNotifications |> List.map mapper }
  |> withCommand cmd

let withoutNotification (notification,transaction,_) model =
  let newNotifications =
     model.OpenNotifications
     |> List.filter (fun (event,tx,_) -> (event = notification && tx = transaction) |> not )

  { model with OpenNotifications = newNotifications }

let private updateWhatIfView editor conference whatif command (behaviour : Conference -> Domain.Events.Event list) =
  let events =
    conference |> behaviour

  let newConference =
    events |> updateStateWithEvents conference

  let whatif =
    WhatIf
      {
        whatif with
          Events = events
          Commands = command :: whatif.Commands
      }

  Edit (editor, newConference, whatif)

let commandEnvelopeForMessage conferenceId msg =
  match msg with
  | Vote voting ->
      API.Command.conferenceApi.Vote voting conferenceId

  | RevokeVoting voting ->
      API.Command.conferenceApi.RevokeVoting voting conferenceId

  | FinishVotingperiod ->
      API.Command.conferenceApi.FinishVotingPeriod conferenceId

  | ReopenVotingperiod ->
      API.Command.conferenceApi.ReopenVotingPeriod conferenceId

  | AddOrganizerToConference organizer ->
      API.Command.conferenceApi.AddOrganizerToConference organizer conferenceId

  | RemoveOrganizerFromConference organizer ->
      API.Command.conferenceApi.RemoveOrganizerFromConference organizer conferenceId

  | ChangeTitle title ->
      API.Command.conferenceApi.ChangeTitle title conferenceId

  | DecideNumberOfSlots number ->
     API.Command.conferenceApi.DecideNumberOfSlots number conferenceId


let eventsForMessage msg =
  match msg with
  | Vote voting ->
      Behaviour.vote voting

  | RevokeVoting voting ->
      Behaviour.revokeVoting voting

  | FinishVotingperiod ->
      Behaviour.finishVotingPeriod

  | ReopenVotingperiod ->
      Behaviour.reopenVotingPeriod

  | AddOrganizerToConference organizer ->
      Behaviour.addOrganizerToConference organizer

  | RemoveOrganizerFromConference organizer ->
      Behaviour.removeOrganizerFromConference organizer

  | ChangeTitle title ->
      Behaviour.changeTitle title

  | DecideNumberOfSlots number ->
      Behaviour.decideNumberOfSlots number

let private updateWhatIf msg editor conference whatif =
  updateWhatIfView
    editor
    conference
    whatif
    (commandEnvelopeForMessage conference.Id msg)
    (eventsForMessage msg)

let private withWsCmd command conference model =
  let transaction =
    transactionId()

  let (ConferenceId eventSource) = conference.Id

  let envelope =
    {
      Transaction = transaction
      EventSource = eventSource
      Command = command
    }

  model
  |> withTransactionSubscription transaction
  |> withCommand (wsCmd (ClientMsg.Command (envelope)))


let sendCommandEnvelope commandEnvelope =
  Cmd.OfAsync.perform (fun () -> commandPort.Handle commandEnvelope)  () CommandResponse

let withLiveUpdateCmd conference msg model =
  let commandEnvelope =
    commandEnvelopeForMessage conference.Id msg

  model
  |> withTransactionSubscription commandEnvelope.Transaction
  |> withCommand (sendCommandEnvelope commandEnvelope)

let update (msg : Msg) (model : Model) : Model * Cmd<Msg> =
  match msg with
  | OrganizersLoaded (Ok organizers) ->
      { model with Organizers = organizers |> RemoteData.Success }
      |> withoutCommands

  | OrganizersLoaded (Result.Error _) ->
      model |> withoutCommands

  | ConferencesLoaded (Ok conferences) ->
      { model with Conferences = conferences |> RemoteData.Success }
      |> withoutCommands

  | ConferencesLoaded (Result.Error _) ->
      model |> withoutCommands

  | ConferenceLoaded (Ok conference) ->
      model
      |> withView ((VotingPanel,conference,Live) |> Edit)
      |> withoutCommands

  | ConferenceLoaded (Result.Error _) ->
      model |> withoutCommands

  | Received (ServerMsg.Connected) ->
      model, Cmd.batch [ queryConferences ; queryOrganizers ]

  | Received (ServerMsg.Events events) ->
      match model.View with
      | Edit (editor, conference, Live) ->
          let newConference =
            events
            |> List.filter (eventIsForConference conference.Id)
            |> List.map (fun envelope -> envelope.Event)
            |> updateStateWithEvents conference

          model
          |> withView ((editor,newConference,Live) |> Edit)
          |> withReceivedEvents events

      | _ ->
          model |> withoutCommands

  | WhatIfMsg msg ->
      match model.View with
      | Edit (_, conference, Live) ->
          model
          |> withLiveUpdateCmd conference msg

      | Edit (editor, conference, WhatIf whatif) ->
          model
          |> withView (updateWhatIf msg editor conference whatif)
          |> withoutCommands

      | _ ->
           model |> withoutCommands

  | MakeItSo ->
      match model.View with
      | Edit (editor, conference, WhatIf whatIf)  ->
          let commands =
            whatIf.Commands
            |> List.rev
            |> List.collect sendCommandEnvelope

          model
          |> withView ((editor,whatIf.Conference,Live) |> Edit)
          |> withTransactionSubscriptions (whatIf.Commands |> List.map (fun commandEnvelope -> commandEnvelope.Transaction))
          |> withCommand (Cmd.batch [commands ; queryConference conference.Id])

      | _ ->
          model |> withoutCommands

  | ToggleMode ->
      match model.View with
      | Edit (editor, conference, Live) ->
          let whatif =
            {
              Conference = conference
              Commands = []
              Events = []
            }

          model
          |> withView ((editor, conference, whatif |> WhatIf) |> Edit)
          |> withoutCommands

      | Edit (editor, conference, WhatIf _) ->
          { model with View = (editor, conference, Live) |> Edit },
          conference.Id |> queryConference

      | _ ->
          model |> withoutCommands

  | SwitchToConference conferenceId ->
      model, conferenceId |> queryConference

  | SwitchToEditor target ->
      match model.View with
      | Edit (_, conference, mode) ->
          let editor =
            match target with
            | AvailableEditor.ConferenceInformation ->
                ConferenceInformation.State.init conference.Title (conference.AvailableSlotsForTalks |> string)
                |> Editor.ConferenceInformation

            | AvailableEditor.VotingPanel ->
                Editor.VotingPanel

            | AvailableEditor.Organizers ->
                Editor.Organizers

          model
          |> withView ((editor, conference, mode) |> Edit)
          |> withoutCommands

      | _ ->
          model |> withoutCommands

  | SwitchToNewConference ->
      model
      |> withView (ConferenceInformation.State.init "" "" |> CurrentView.ScheduleNewConference)
      |> withoutCommands

  | ResetConferenceInformation ->
      match model.View with
      | Edit (ConferenceInformation _, conference, mode) ->
          let editor =
            ConferenceInformation.State.init conference.Title (conference.AvailableSlotsForTalks |> string)
            |> Editor.ConferenceInformation

          model
          |> withView ((editor, conference, mode) |> Edit)
          |> withoutCommands

      | _ ->
          model |> withoutCommands

  | UpdateConferenceInformation ->
      match model.View with
      | Edit (ConferenceInformation submodel, conference, _) when submodel |> ConferenceInformation.Types.isValid ->
          let title =
            submodel |> ConferenceInformation.Types.title

          let titleCmd =
            if title <> conference.Title then
              title
              |> ChangeTitle
              |> WhatIfMsg
              |> Cmd.ofMsg
            else
              Cmd.none

          let availableSlotsForTalks =
            submodel |> ConferenceInformation.Types.availableSlotsForTalks

          let availableSlotsForTalksCmd =
            if availableSlotsForTalks <> conference.AvailableSlotsForTalks then
              availableSlotsForTalks
              |> DecideNumberOfSlots
              |> WhatIfMsg
              |> Cmd.ofMsg
            else
              Cmd.none

          model
          |> withCommand (Cmd.batch [ titleCmd ; availableSlotsForTalksCmd ])

      | _ ->
          model |> withoutCommands

  | Msg.ScheduleNewConference ->
      match model.View with
      | ScheduleNewConference submodel when submodel |> ConferenceInformation.Types.isValid ->
          let title =
            submodel |> ConferenceInformation.Types.title

          let availableSlotsForTalks =
            submodel |> ConferenceInformation.Types.availableSlotsForTalks

          let conference =
            emptyConference()
            |> withTitle title
            |> withAvailableSlotsForTalks availableSlotsForTalks

          let command =
            conference |> Commands.ScheduleConference

          let editor =
            ConferenceInformation.State.init conference.Title (conference.AvailableSlotsForTalks |> string)
            |> Editor.ConferenceInformation

          model
          |> withView ((editor, conference, Live) |> Edit)
          |> withWsCmd command conference

      | _ ->
          model |> withoutCommands

  | ConferenceInformationMsg msg ->
      match model.View with
      | Edit (ConferenceInformation submodel, conference, mode) ->
          let newSubmodel =
            submodel |> ConferenceInformation.State.update msg

          model
          |> withView ((ConferenceInformation newSubmodel, conference, mode) |> Edit)
          |> withoutCommands

      | ScheduleNewConference submodel ->
          let view =
            submodel
            |> ConferenceInformation.State.update msg
            |> ScheduleNewConference

          model
          |> withView view
          |> withoutCommands

      | _ ->
          model |> withoutCommands

  | RequestNotificationForRemoval notification ->
      model
      |> withRequestedForRemovalNotification notification

  | RemoveNotification notification ->
      model
      |> withoutNotification notification
      |> withoutCommands

  | CommandResponse _ ->
      // TODO: damit umgehen
      model |> withoutCommands
