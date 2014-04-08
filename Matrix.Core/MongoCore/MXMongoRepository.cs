﻿using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Driver.Linq;
using System.Linq.Expressions;
using Matrix.Core.MongoCore;

namespace Matrix.Core.FrameworkCore
{
    /// <summary>
    /// Generic repository class for accessing MongoDB.
    /// </summary>
    public class MXMongoRepository : IRepository
    {
        #region "Initialization and attributes"

        //lazy instantiation; do not create the context unless required. This would be useful in scenarios where database is not accessed, say static pages
        Lazy<MongoDatabase> _dbContext = new Lazy<MongoDatabase>(() => new MXMongoContext().DbContext);

        protected MongoDatabase dbContext
        {
            get { return _dbContext.Value; }
        }

        public MXMongoRepository(){ }

        #endregion

        #region "Interface implementaions; generic CRUD repository"

        public virtual string Insert<T>(T entity) where T : IMXEntity
        {
            entity.IsActive = true;

            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            collection.Insert(entity, WriteConcern.Acknowledged);

            return entity.Id;
        }

        /// <summary>
        /// Batch insert
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entities"></param>
        /// <returns></returns>
        public virtual bool Insert<T>(IList<T> entities) where T : IMXEntity
        {
            foreach (var entity in entities) entity.IsActive = true;

            var collection = dbContext.GetCollection<T>(typeof(T).Name);
            
            var result = collection.InsertBatch(entities, WriteConcern.Acknowledged);
            
            return result.All(c => c.Ok == true);
        }

        /// <summary>
        /// Bulk insert
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entities"></param>
        /// <returns>Inserted count</returns>
        public virtual long BulkInsert<T>(IList<T> entities) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            var bulk = collection.InitializeUnorderedBulkOperation();
            
            foreach (var entity in entities)
                bulk.Insert<T>(entity);

            return bulk.Execute(WriteConcern.Acknowledged).InsertedCount;
        }

        public virtual T GetOne<T>(string id) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);
            
            return collection.FindOneById(new ObjectId(id));
        }

        /// <summary>
        /// Load records based on predicates
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="predicate">Use the MXPredicate object to build predicates</param>
        /// <param name="limit"></param>
        /// <returns></returns>
        public virtual IList<T> GetMany<T>(Expression<Func<T, bool>> predicate = null, bool bIsActive = true, int take = 128, int skip = 0) where T : IMXEntity
        {   
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            if (predicate == null)
                return collection.AsQueryable().Where(c => c.IsActive == bIsActive).Skip(skip).Take(take).ToList();
            else
            {
                predicate = predicate.And(p => p.IsActive == bIsActive);
                return collection.AsQueryable().Where(predicate).Skip(skip).Take(take).ToList();
            }
        }

        /// <summary>
        /// Update while giving option for maintaining history
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity"></param>
        /// <returns></returns>
        public virtual bool Update<T>(T entity, bool bMaintainHistory = false) where T : IMXEntity            
        {
            var collectionName = typeof(T).Name;

            var collection = dbContext.GetCollection<T>(collectionName);

            if (bMaintainHistory)
            {
                var historyDoc = GetOne<T>(entity.Id);

                Task.Run(() =>
                    InsertDocumentIntoHistory<T>(historyDoc)
                );
            }            

            var t = collection.Save<T>(entity, WriteConcern.Acknowledged);

            return t.Ok;            
        }

        public virtual long BulkUpdate<T>(IMongoQuery query, IMongoUpdate update, bool bMaintainHistory = false) where T : IMXEntity
        {
            var collectionName = typeof(T).Name;

            var collection = dbContext.GetCollection<T>(collectionName);

            var bulk = collection.InitializeOrderedBulkOperation();

            if (bMaintainHistory)
            {
                var historyDocs = collection.FindAs<T>(query).ToList();

                Task.Run(() =>
                    InsertMultipleDocumentsIntoHistory<T>(historyDocs)
                );
            }       

            bulk.Find(query).Update(update);

            return bulk.Execute(WriteConcern.Acknowledged).ModifiedCount;
        }

        public virtual bool Delete<T>(string id) where T : IMXEntity
        {   
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            var query = Query<T>.EQ(e => e.Id, id);
            var result = collection.Remove(query);

            return result.Ok;
        }

        public virtual bool Delete<T>(IList<string> ids) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            var query = Query<T>.In<string>(e => e.Id, ids);
            var result = collection.Remove(query);

            return result.Ok;
        }

        #endregion

        #region "Full text search"

        /// <summary>
        /// Equivalent to a term query in lucene; a great feature. Wild card searches are not supported at the moment with a text index.
        /// Also do not forget to create a text index on the collection referred; eg. db.Author.ensureIndex({nm : "text"})
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="term"></param>
        /// <param name="take"></param>
        /// <param name="skip"></param>
        /// <returns></returns>
        public virtual IList<T> GetManyByTextSearch<T>(string term, int take = 128, int skip = 0) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            var query = Query.And(Query.Text(term), Query<T>.EQ(e => e.IsActive, true));

            return collection.FindAs<T>(query).Skip(skip).Take(take).ToList();
        }

        #endregion

        #region "Other methods; AlterStatus() etc"

        public virtual string GetNameById<T>(string Id) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            return collection.AsQueryable().Where(c => c.Id == Id).SingleOrDefault().Name;
        }

        public virtual DenormalizedReference GetOptionById<T>(string Id) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            return collection.AsQueryable().Where(c => c.Id == Id).Select(c => new DenormalizedReference { DenormalizedId = c.Id, DenormalizedName = c.Name }).SingleOrDefault();
        }

        /// <summary>
        /// Get optionSets based on a predicate
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="predicate">IsActive == true is by default. when null, this returns all items</param>
        /// <param name="take">Effective only when there is a predicate</param>
        /// <returns></returns>
        public virtual IList<DenormalizedReference> GetOptionSet<T>(Expression<Func<T, bool>> predicate = null, int take = 15) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            if (predicate == null)
                return collection.AsQueryable()
                    .Where(c => c.IsActive == true)
                    .Select(c => new DenormalizedReference { DenormalizedId = c.Id, DenormalizedName = c.Name })
                    .OrderBy(c => c.DenormalizedName)
                    .ToList();
            else
            {
                predicate = predicate.And(p => p.IsActive == true);
                return collection.AsQueryable().Where(predicate)
                    .Select(c => new DenormalizedReference { DenormalizedId = c.Id, DenormalizedName = c.Name })
                    .OrderBy(c => c.DenormalizedName).Take(take)
                    .ToList();
            }
        }

        public virtual bool AlterStatus<T>(string id, bool statusValue) where T : IMXEntity
        {
            var collection = dbContext.GetCollection<T>(typeof(T).Name);

            var query = Query<T>.EQ(e => e.Id, id);
                        
            var update = MongoDB.Driver.Builders.Update<T>.Set(c => c.IsActive, statusValue);

            var result = collection.Update(query, update, WriteConcern.Acknowledged);

            return result.Ok;
        }

        /// <summary>
        /// Return the count of Active Records only
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public virtual long GetCount<T>(Expression<Func<T, bool>> predicate = null) where T : IMXEntity
        {            
            var collection = dbContext.GetCollection<T>(typeof(T).Name);
                        
            if (predicate == null)
                return collection.AsQueryable().Where(c => c.IsActive == true).Count();
            else
            {
                predicate = predicate.And(p => p.IsActive == true);
                return collection.AsQueryable().Where(predicate).Count();
            }            
        }

        #endregion

        #region "protected Helpers"

        protected void InsertDocumentIntoHistory<T>(T entity) where T : IMXEntity
        {            
            MXMongoEntityX<T> tX = new MXMongoEntityX<T>
            {
                XDocument = entity,
            };

            var collectionX = dbContext.GetCollection<T>(typeof(T).Name + 'X');
            collectionX.Insert(tX);
        }

        /// <summary>
        /// Insert into history. Call this before updating the document.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="id"></param>
        protected void InsertDocumentIntoHistory<T>(string id) where T : IMXEntity
        {
            var entity = GetOne<T>(id);

            MXMongoEntityX<T> tX = new MXMongoEntityX<T>
            {
                XDocument = entity,
            };

            var collectionX = dbContext.GetCollection<T>(typeof(T).Name + 'X');
            collectionX.Insert(tX);
        }

        protected void InsertMultipleDocumentsIntoHistory<T>(IList<T> entities) where T : IMXEntity
        {
            List<MXMongoEntityX<T>> xDocs = new List<MXMongoEntityX<T>>();

            foreach (var doc in entities)
            {
                MXMongoEntityX<T> xDoc = new MXMongoEntityX<T> 
                {
                    XDocument = doc
                };
            }

            var collectionX = dbContext.GetCollection<T>(typeof(T).Name + 'X');

            var bulk = collectionX.InitializeUnorderedBulkOperation();

            foreach (var doc in xDocs)
                bulk.Insert(doc);

            bulk.Execute(WriteConcern.Acknowledged);
        }

        #endregion

    }//End of class CRUDBase
}
